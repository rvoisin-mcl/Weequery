using System.Linq.Expressions;
using System.Text.RegularExpressions;
using Weequery.Builders;
using Weequery.Interfaces;

namespace Weequery;

/// <summary>
/// Wrapper class that provides fluent configuration for IQueryable with Weequery
/// </summary>
/// <remarks>
/// <para>
/// The bound properties are the allow-list: a condition or a sort naming a field that no binding claimed is
/// refused. Field names are matched against binding keys without regard to case.
/// </para>
/// <para>
/// <b>An Inquiry is mutable, and it is the one thing about this library that reads like it is not.</b> Every
/// Apply and every Bind changes the object and returns the same one, so a fluent chain is a sequence of
/// modifications rather than a pipeline of new values. That is not how <see cref="IQueryable{T}"/> behaves, and
/// the chain looks enough like a LINQ chain to invite the assumption.
/// </para>
/// <para>
/// It matters when one is kept and used twice. Conditions <b>accumulate</b>, sorts accumulate, and everything
/// else is last-call-wins, so a second query built off the same Inquiry carries the first one's filter with it:
/// <code>
/// var inquiry = query.WithWeequery().BindProperties(MinionBindings);
///
/// var active = inquiry.ApplyCondition("IsActive = true").Build();
/// var paid   = inquiry.ApplyCondition("Pay &gt; 10000").Build();    // active AND paid, not paid
/// </code>
/// Which is an answer rather than an error, and a plausible looking one, so nothing tells you.
/// </para>
/// <para>
/// Build one per query, which is what the usual shape does anyway, or <see cref="Clone"/> a configured one and
/// branch off the copy.
/// </para>
/// </remarks>
/// <typeparam name="T"></typeparam>
public class Inquiry<T> where T : class
{
    private IQueryable<T> Query { get; init; }

    /// <summary>
    /// The "x" every accessor hangs off, one per entity type rather than one per query.
    /// <para>
    /// All the bindings used together have to share it: a lambda is built from one binding's parameter and a body
    /// assembled from several, so accessors rooted in different parameters would not compose. Sharing it for the
    /// whole type is what lets a binding built once be used by every query after it, see
    /// <see cref="BindingSetCache{T}"/>, and costs nothing to do — an expression tree is immutable, and a parameter is an
    /// identity rather than a value, so two lambdas built over the same one are still two independent lambdas.
    /// </para>
    /// <para>
    /// The one place it would matter is a lambda nested inside another over the same type, where the inner one
    /// would rebind the parameter and shadow the outer. Weequery never builds that shape; a caller composing two
    /// predicates of its own into one is the only way to reach it, see the remarks on
    /// <see cref="BuildExpression"/>.
    /// </para>
    /// </summary>
    private static readonly ParameterExpression SharedBindingParameter = Expression.Parameter(typeof(T));

    private Dictionary<string, Binding<T>> Bindings { get; init; } = BindingLookup.Create<T>();

    /// <summary>
    /// The bound collections, kept apart from the properties because they answer a different kind of question.
    /// <para>
    /// A collection is not something the comparison operators can be asked, and a property is not something a
    /// quantifier can be asked, so one lookup would only mean each of them refusing half its entries. Keyed the
    /// same way, so a collection and a property still cannot share a name, see <see cref="BindCollection"/>.
    /// </para>
    /// </summary>
    private Dictionary<string, ICollectionBinding<T>> Collections { get; init; } = new(BindingLookup.KeyComparer);

    private List<ICondition> Conditions { get; init; } = new();
    private List<Sort> Sorts { get; init; } = new();

    /// <summary>
    /// Which fields <see cref="BuildProjected"/> reads back, see <see cref="ApplyProjection(string?)"/>. Empty
    /// until a caller says otherwise, which reads every bound field.
    /// </summary>
    private Projection Projected { get; set; } = Projection.None;

    /// <summary>
    /// The projection a caller applied, or <see cref="Projection.None"/> where none was, see
    /// <see cref="ApplyProjection(string?)"/>.
    /// </summary>
    /// <remarks>
    /// Readable so that something building on this Inquiry can tell if the caller asked for a set of
    /// columns, which is what a different way of shaping a row has to know before it quietly ignores one. The
    /// AutoMapper package refuses that combination on exactly this.
    /// </remarks>
    public Projection AppliedProjection { get { return Projected; } }

    private int PageSize { get; set; } = -1;
    private int Page { get; set; } = -1;

    /// <summary>
    /// If a field nothing bound is dropped rather than refused, see <see cref="IgnoreUnboundFields"/>. Off,
    /// which is the answer that never surprises anyone.
    /// </summary>
    private bool DropsUnboundFields { get; set; }

    private List<DroppedField> Dropped { get; init; } = new();

    /// <summary>
    /// What the last build took out of the query for naming a field nothing bound, see
    /// <see cref="IgnoreUnboundFields"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Empty unless dropping was asked for, since nothing is dropped otherwise. Filled in by whichever of
    /// <see cref="Build"/>, <see cref="BuildPaged"/>, <see cref="BuildProjected"/> and
    /// <see cref="BuildPagedProjected"/> was called, and <b>reset by each of them</b>, so it describes the query
    /// you have rather than accumulating across builds.
    /// </para>
    /// <para>
    /// Populated while the query is being built rather than when it is enumerated, which is the same point
    /// everything else is resolved at, so it is ready as soon as the build returns and before a single row has
    /// been read.
    /// </para>
    /// <code>
    /// var rows = inquiry.Build().ToList();
    ///
    /// if (inquiry.DroppedFields.Count > 0)
    /// {
    ///     // "Filter on 'Gizmo' no longer applies and has been removed from your saved view"
    ///     Warn(inquiry.DroppedFields);
    /// }
    /// </code>
    /// <para>
    /// A field named in two places is reported once for each, so a query filtering and sorting on the same
    /// missing key gives two entries. One named twice in the same place is reported once.
    /// </para>
    /// </remarks>
    public IReadOnlyList<DroppedField> DroppedFields { get { return Dropped; } }

    /// <summary>
    /// Note a field as dropped, unless that part of the query has already lost it.
    /// </summary>
    /// <remarks>
    /// The same key can genuinely be dropped from two parts of a query, and both are worth saying. What is not
    /// worth saying twice is one part losing it twice, which is what a projected field read by two of the build
    /// methods, or a key written twice in one condition, would otherwise produce.
    /// </remarks>
    private void Drop(string field, BindingUse from)
    {
        // The index goes: an unbound "Tallies[apples]" is one missing binding called Tallies rather than a missing
        // element, and naming it that way is what lets two indexes into the same absent collection say it once
        (field, _) = BindingLookup.SplitIndex(field);

        if (Dropped.Any(entry => (entry.From == from) && BindingLookup.KeyComparer.Equals(entry.Field, field))) { return; }

        Dropped.Add(new DroppedField(field, from));
    }

    /// <summary>
    /// How long <see cref="Operator.IsMatch"/> may spend on one value before giving up, when the match runs in
    /// this process. One second by default; assign to change it, or
    /// <see cref="Regex.InfiniteMatchTimeout"/> to remove the bound.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A pattern is caller input, and a regular expression can be made to cost far more than it looks: matching
    /// <c>(a+)+$</c> against a few dozen characters that do not match takes time exponential in their number.
    /// Every other operator is bounded by the size of the data, so this is the one that can turn a filter into a
    /// denial of service, and it is bounded rather than left to run.
    /// </para>
    /// <para>
    /// A value that exceeds it raises <see cref="RegexMatchTimeoutException"/> from wherever the query is being
    /// enumerated, rather than a <see cref="WeequeryException"/>: it is the framework reporting what it stopped
    /// doing, and no answer is available for that row.
    /// </para>
    /// <para>
    /// This bounds the match only where Weequery runs it, which is in memory. Translated to SQL the pattern is
    /// the database's to run and its own limits apply, see <see cref="Operator.IsMatch"/>. The reason it cannot
    /// be both is that the overload of
    /// <see cref="Regex.IsMatch(string, string, RegexOptions, TimeSpan)"/> carrying a timeout is not one any
    /// provider translates, so an expression built with it would stop being a query and start being a table
    /// scan on the client.
    /// </para>
    /// </remarks>
    public static TimeSpan MatchTimeout { get; set; } = TimeSpan.FromSeconds(1);

    internal Inquiry(IQueryable<T> query)
    {
        Query = query;
    }

    /// <summary>
    /// A copy of this Inquiry, so one can be configured once and then branched rather than reused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The way out of the one hazard this type has, see the remarks on <see cref="Inquiry{T}"/>: an Inquiry is
    /// mutable and its conditions accumulate, so a second query built off the same one carries the first one's
    /// filter. Cloning gives each query its own.
    /// <code>
    /// var bound = query.WithWeequery().BindProperties(MinionBindings);
    ///
    /// var active = bound.Clone().ApplyCondition("IsActive = true").Build();
    /// var paid   = bound.Clone().ApplyCondition("Pay &gt; 10000").Build();
    /// </code>
    /// </para>
    /// <para>
    /// <b>What is copied is the lists, not what is in them.</b> A binding is immutable once built and is shared
    /// rather than rebuilt, which is the whole reason cloning is cheap: adding a binding to the copy leaves the
    /// original's set alone, and the bindings both of them already had are the same objects. The same goes for
    /// the conditions, which are yours and are shared as you handed them over; a condition you go on to mutate
    /// is mutated for both, as it would be for two queries you built without this.
    /// </para>
    /// <para>
    /// <see cref="DroppedFields"/> is not copied. It describes a build rather than a configuration, and the copy
    /// has not built anything.
    /// </para>
    /// </remarks>
    /// <returns>a new Inquiry over the same query, configured the same way, sharing nothing mutable</returns>
    public Inquiry<T> Clone()
    {
        return new Inquiry<T>(Query)
        {
            Bindings = new Dictionary<string, Binding<T>>(Bindings, BindingLookup.KeyComparer),
            Collections = new Dictionary<string, ICollectionBinding<T>>(Collections, BindingLookup.KeyComparer),
            Conditions = [.. Conditions],
            Sorts = [.. Sorts],
            Projected = Projected,
            PageSize = PageSize,
            Page = Page,
            DropsUnboundFields = DropsUnboundFields,
        };
    }

    /// <summary>
    /// Bind the property indicated by the selector func, if a key is not provided, it will be bound as the property path
    /// </summary>
    /// <typeparam name="TProperty"></typeparam>
    /// <param name="selector"></param>
    /// <param name="key"></param>
    /// <returns></returns>
    /// <param name="use">[OPT] what the binding may be used for, all three by default, see <see cref="BindingUse"/></param>
    /// <param name="convert">[OPT] a normalisation applied to its values, see <see cref="ValueConverter"/></param>
    public Inquiry<T> BindProperty<TProperty>(Expression<Func<T, TProperty>> selector, string? key = null, BindingUse use = BindingUse.All, ValueConverter? convert = null)
    {
        Binding<T>.Create(SharedBindingParameter, selector, Bindings, key, use, convert);

        return RefuseDuplicateKeys();
    }

    /// <summary>
    /// Bind the property reached by following the selector and then the segments after it, for a path a selector
    /// cannot write on its own. If a key is not provided, the whole path is used, a period being a legal key
    /// character.
    /// </summary>
    /// <remarks>
    /// The case this exists for is a path through a <see cref="Nullable{T}"/>: C# will not compile
    /// <c>(x) =&gt; x.BirthDate.Year</c> against a <c>DateTime?</c>, since a Nullable exposes only its own members,
    /// and writing <c>(x) =&gt; x.BirthDate!.Value.Year</c> instead unwraps rather than reaches through, binding a
    /// plain int with no null of its own. Selecting BirthDate and naming "Year" as a segment binds
    /// "BirthDate.Year" exactly as <see cref="BindProperty(string, string?, BindingUse, ValueConverter)"/> would, while the compiler still
    /// checks the part of the path it can see. See the remarks on <see cref="Operator"/> for what reaching through
    /// a nullable means for the operators.
    /// <code>
    /// .BindProperty(minion =&gt; minion.BirthDate, ["Year"], "BirthYear")
    /// </code>
    /// </remarks>
    /// <typeparam name="TProperty"></typeparam>
    /// <param name="selector">as far as the compiler can follow</param>
    /// <param name="segments">the rest of the path, in order</param>
    /// <param name="key"></param>
    /// <returns></returns>
    /// <param name="use">[OPT] what the binding may be used for, all three by default, see <see cref="BindingUse"/></param>
    /// <param name="convert">[OPT] a normalisation applied to its values, see <see cref="ValueConverter"/></param>
    /// <exception cref="WeequeryException"></exception>
    public Inquiry<T> BindProperty<TProperty>(Expression<Func<T, TProperty>> selector, string[] segments, string? key = null, BindingUse use = BindingUse.All, ValueConverter? convert = null)
    {
        Binding<T>.Create(SharedBindingParameter, selector, segments, Bindings, key, use, convert);

        return RefuseDuplicateKeys();
    }

    /// <summary>
    /// Bind a collection, and declare what may be asked about one of its elements, so a caller can ask if
    /// <see cref="Operator.Any"/>, <see cref="Operator.All"/> or <see cref="Operator.None"/> of them match.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <code>
    /// .BindCollection(minion =&gt; minion.Assignments, "Assignments", inner =&gt; inner
    ///     .BindProperty(assignment =&gt; assignment.LairID)
    ///     .BindProperty(assignment =&gt; assignment.Lair.Name, "LairName"))
    ///
    /// .ApplyCondition("Assignments Any (LairID = 5 AND LairName StartsWith 'V')")
    /// </code>
    /// </para>
    /// <para>
    /// <b>The inside is its own allow-list.</b> Binding the collection exposes nothing within it; what a caller
    /// may name inside the brackets is what the inner configuration bound, and nothing else. That is the same
    /// rule the outer bindings follow, applied one level down, and it is why this takes a configuration rather
    /// than reaching into the element type on its own.
    /// </para>
    /// <para>
    /// The condition inside is scoped to <b>one element</b>, which is the whole reason a quantifier holds a
    /// condition rather than the caller writing two of them: "Any (LairID = 5 AND IsPrimary = true)" asks for one
    /// assignment that is both, where two separate quantifiers ANDed together ask only that each is true of some
    /// assignment, possibly different ones.
    /// </para>
    /// <para>
    /// The collection itself is not otherwise answerable: it is not bound as a property, so it takes no
    /// comparison and no index, and a caller naming it outside a quantifier is refused. Bind it with
    /// <see cref="BindProperty{TProperty}(Expression{Func{T, TProperty}}, string?, BindingUse, ValueConverter)"/> as well if you also want
    /// it tested for null or indexed, under a different key.
    /// </para>
    /// <para>
    /// <b>If this reaches a database is the provider's business.</b> A quantifier becomes Any or All over
    /// the collection, which EF Core translates to EXISTS against a navigation collection. See the remarks on
    /// <see cref="Operator.Any"/> for what it means over one that is empty or missing.
    /// </para>
    /// </remarks>
    /// <typeparam name="TElement">what the collection holds</typeparam>
    /// <param name="selector">the collection on this entity</param>
    /// <param name="key">the name a caller refers to it by</param>
    /// <param name="configure">
    /// what may be asked about an element. Called once, on an empty set; a collection with nothing bound inside
    /// is refused, since no condition could ever be written against it
    /// </param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">
    /// the key is not one, or is already taken, or the property is not a collection, or nothing was bound inside
    /// </exception>
    public Inquiry<T> BindCollection<TElement>(
        Expression<Func<T, IEnumerable<TElement>?>> selector,
        string key,
        Action<CollectionBindingSet<TElement>> configure)
        where TElement : class
    {
        WeequeryException.ThrowIfNull(selector);
        WeequeryException.ThrowIfNullOrEmpty(key);
        WeequeryException.ThrowIfNotBindingKey(key);
        WeequeryException.ThrowIfNull(configure);

        // One name for one thing, whichever of the two lookups it lands in
        if (Bindings.ContainsKey(key) || Collections.ContainsKey(key))
        {
            throw new WeequeryException(WeequeryError.KeyTaken, $"Binding already exists for '{key}'");
        }

        // Not added to Bindings: a collection answers a quantifier and nothing else, and putting it there would
        // offer it to every operator that cannot use it
        var collection = Binding<T>.Create(SharedBindingParameter, selector, bindings: null);

        var inner = new CollectionBindingSet<TElement>();
        configure(inner);

        if (inner.Count == 0)
        {
            throw new WeequeryException(WeequeryError.BindingInvalid, $"Nothing was bound inside '{key}', so no condition could be written about one of its elements");
        }

        Collections[key] = new CollectionBinding<T, TElement>(key, collection, inner.Bindings);

        return this;
    }

    /// <summary>
    /// Refuse a property binding whose key a collection has already claimed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two lookups are kept apart so each can refuse what the other answers, see <see cref="Collections"/>,
    /// but a name still only means one thing, so a key in both is refused. <see cref="BindCollection"/> checks
    /// both before it adds, and this is the other direction: the key a property binding uses may be one the
    /// caller named or one the binding derived, and neither is known until it has been made.
    /// </para>
    /// <para>
    /// What a binding may be <i>used</i> for needs nothing here, being carried on the binding itself, see
    /// <see cref="BindingUse"/>. A key means one thing whatever it is allowed to do with it.
    /// </para>
    /// <para>
    /// The colliding property binding is taken back off before throwing, so an Inquiry a caller went on to use
    /// after catching this is in the state it was in before the call rather than half changed.
    /// </para>
    /// </remarks>
    /// <returns>this, so it can be returned from the binding call</returns>
    /// <exception cref="WeequeryException">a key now names both a property and a collection</exception>
    private Inquiry<T> RefuseDuplicateKeys()
    {
        if (Collections.Count == 0) { return this; }

        foreach (var key in Collections.Keys)
        {
            if (Bindings.Remove(key))
            {
                throw new WeequeryException(WeequeryError.KeyTaken, $"Binding already exists for '{key}', which is bound as a collection");
            }
        }

        return this;
    }

    /// <summary>
    /// Bind a value under a name, rather than a property. A caller can then compare a property against it by name
    /// without having to say what it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pairing this exists for is a comparison against a bound property, see
    /// <see cref="ConditionValue{T}"/>: the caller writes "Pay &gt; [Threshold]" and the application decides what
    /// Threshold is, per request, per tenant, or per anything else it knows and the caller does not.
    /// <code>
    /// .BindConstant("Threshold", payThreshold)
    /// .ApplyCondition("Pay &gt; [Threshold]")
    /// </code>
    /// </para>
    /// <para>
    /// As it is a constant value, a sort on it is refused.
    /// </para>
    /// <para>
    /// Deliberately not part of <see cref="BindingRequest"/>, as those are cached for the life of the process, 
    /// which may not be ideal for a value that may differ from one request to the next.
    /// </para>
    /// </remarks>
    /// <typeparam name="TValue"></typeparam>
    /// <param name="key">the name a caller refers to it by</param>
    /// <param name="value">must not be null: a constant stands for a value, so it needs one</param>
    /// <returns></returns>
    /// <param name="use">[OPT] what the binding may be used for, all three by default, see <see cref="BindingUse"/></param>
    /// <param name="convert">[OPT] a normalisation applied to its value, see <see cref="ValueConverter"/></param>
    /// <exception cref="WeequeryException"></exception>
    public Inquiry<T> BindConstant<TValue>(string key, TValue value, BindingUse use = BindingUse.Condition | BindingUse.Projection, ValueConverter? convert = null)
    {
        WeequeryException.ThrowIfNullOrEmpty(key);
        WeequeryException.ThrowIfNotBindingKey(key);

        Binding<T>.CreateConstant(SharedBindingParameter, key, value, Bindings, use, convert);

        return RefuseDuplicateKeys();
    }

    /// <summary>
    /// Bind the property with the provided path, if a key is not provided, it will be bound as the property path
    /// </summary>
    /// <param name="path"></param>
    /// <param name="key"></param>
    /// <returns></returns>
    /// <param name="use">[OPT] what the binding may be used for, all three by default, see <see cref="BindingUse"/></param>
    /// <param name="convert">[OPT] a normalisation applied to its values, see <see cref="ValueConverter"/></param>
    public Inquiry<T> BindProperty(string path, string? key = null, BindingUse use = BindingUse.All, ValueConverter? convert = null)
    {
        WeequeryException.ThrowIfNullOrEmpty(path);
        WeequeryException.ThrowIfNotNullButEmpty(key);
        WeequeryException.ThrowIfNotBindingKey(key);

        Binding<T>.Create(SharedBindingParameter, path, Bindings, key, use, convert);

        return RefuseDuplicateKeys();
    }

    /// <summary>
    /// Bind the properties in the list, if a key is not provided, they will be bound as the property path
    /// </summary>
    /// <remarks>
    /// A set of requests is resolved once for the process and kept, see <see cref="BindingSetCache{T}"/>, so calling this
    /// per request costs a copy rather than a property path lookup per property. Adding to this Inquiry after it,
    /// with this or with <see cref="BindProperty(string, string?, BindingUse, ValueConverter)"/>, works as it always did: everything binds
    /// against the same parameter either way.
    /// </remarks>
    /// <param name="bindingRequests"></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException"></exception>
    public Inquiry<T> BindProperties(IEnumerable<BindingRequest> bindingRequests)
    {
        WeequeryException.ThrowIfNull(bindingRequests);

        // Copied in rather than used as it stands, since this Inquiry's lookup can keep taking bindings after
        // this call and the kept set has to stay as it is
        foreach (var binding in BindingSetCache<T>.For(bindingRequests, SharedBindingParameter))
        {
            if (Bindings.TryGetValue(binding.Key, out var existing))
            {
                // The rule AddTo follows, so a duplicate is answered the same way whichever route it arrives by
                if (!Binding<T>.IsSameBinding(existing, binding.Value)) { throw new WeequeryException(WeequeryError.KeyTaken, $"Binding already exists for '{binding.Key}'"); }

                continue;
            }

            Bindings[binding.Key] = binding.Value;
        }

        return RefuseDuplicateKeys();
    }


    /// <summary>
    /// Build a binding request for every readable property a type reaches, keyed by its path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the opposite of an allow-list, and it is worth stopping on.</b> Everywhere else in this library
    /// you say what may be asked about and everything else is refused. This says "all of it", so every property
    /// name on the type, and on every type it reaches, goes on the wire for a caller to read and filter by. That
    /// is a reasonable thing to want for an internal tool over a model you control, and an unreasonable thing to
    /// do to an entity with an audit trail, a password hash or another tenant's rows hanging off it. Use
    /// <see cref="BindingResolutionSettings"/> to subtract, or write the bindings out and know what they are.
    /// </para>
    /// <para>
    /// A nested property is keyed by its whole dotted path, "Lair.Capacity" rather than "Capacity", which is a
    /// legal key because a period is a legal key character, see
    /// <see cref="WeequeryException.ThrowIfNotBindingKey"/>. Paths are distinct by construction, so nothing here
    /// collides with anything else here; it can still collide with a binding already made by hand, see
    /// <see cref="BindResolve"/>.
    /// </para>
    /// <para>
    /// A property whose path spells an operator is bound with an underscore after it, so a model holding a
    /// property called Contains resolves it as "Contains_" rather than refusing the whole model, see
    /// <see cref="BindingResolver.KeyFor"/>. Only a top level property can collide, a nested "Lair.Contains" being one word to
    /// the tokenizer and not the operator.
    /// </para>
    /// <para>
    /// Three things about the walk are worth knowing before you trust the result:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// It does not descend into a struct, so DateTime.Year is not reached this way and still has to be bound by
    /// hand, see <see cref="BindProperty{TProperty}(Expression{Func{T, TProperty}}, string[], string?, BindingUse, ValueConverter)"/>. A
    /// collection is descended as the class it is rather than as its element type, so filtering into one is no
    /// more possible here than it is anywhere else in this library, and what you get from one is Count and
    /// Capacity, which a provider may well refuse to translate.
    /// </description></item>
    /// <item><description>
    /// A type already open on the path is not entered again, so a model that refers back to itself terminates
    /// rather than multiplying. Two properties of the same type are both expanded; the same type twice down one
    /// chain is not, so "Parent.Parent" is never resolved.
    /// </description></item>
    /// <item><description>
    /// The cycle guard bounds depth, not width. A wide model still grows with
    /// <paramref name="maxDepth"/> a level at a time, which is why the default is low rather than the limit.
    /// </description></item>
    /// </list>
    /// </remarks>
    /// <param name="maxDepth">
    /// how many levels below the entity to reach, so 0 for its own properties and nothing nested, 1 for their
    /// properties as well. Defaults to 1; bounded to [0, 16] rather than refused, so a larger number is quietly
    /// the limit
    /// </param>
    /// <param name="settings">
    /// [OPT] what to leave out; null leaves out
    /// nothing but does not expand a string into its Length
    /// </param>
    /// <returns>the requests, in path order, ready for <see cref="BindProperties"/></returns>
    /// <exception cref="WeequeryException">a resolved path does not make a valid key</exception>
    public static IReadOnlyList<BindingRequest> ResolveBindables(int maxDepth = 1, BindingResolutionSettings? settings = null)
    {
        maxDepth = Math.Min(Math.Max(maxDepth, 0), 16); // bound to [0,16]

        // A caller who named no settings gets the standard ones. Handled here rather than in the copy
        // constructor, which is for copying something.
        settings = (settings is null) ? BindingResolutionSettings.Default : new(settings);

        return BindingResolver.ResolveBindables(new List<BindingRequest>(), typeof(T), 0, maxDepth, "", settings, new HashSet<Type>());
    }

    /// <summary>
    /// Bind every readable property this entity reaches, as <see cref="ResolveBindables(int, BindingResolutionSettings)"/> resolves them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read the warning on <see cref="ResolveBindables(int, BindingResolutionSettings)"/> first.</b> This is the allow-list saying yes to
    /// everything, which is a decision rather than a shortcut.
    /// </para>
    /// <para>
    /// Adds to whatever is already bound rather than replacing it, so a key resolved here that a
    /// <see cref="BindProperty(string, string?, BindingUse, ValueConverter)"/> call already claimed is a duplicate and is refused, see
    /// <see cref="BindProperties"/>. Resolve first and <see cref="RemoveBinding"/> what you do not want, or bind
    /// by hand and do not call this.
    /// </para>
    /// </remarks>
    /// <param name="maxDepth">
    /// how many levels below the entity to reach. Defaults to 1, and bounded to [0, 16]; a deep model is worth
    /// resolving once and looking at before you raise it
    /// </param>
    /// <param name="settings">
    /// [OPT] what to leave out; null leaves nothing out, but does not expand a string into its Length
    /// </param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">a resolved path does not make a valid key, or two bindings claim one key</exception>
    public Inquiry<T> BindResolve(int maxDepth = 1, BindingResolutionSettings? settings = null)
    {
        var reqs = ResolveBindables(maxDepth, settings);

        BindProperties(reqs);

        return this;
    }

    /// <summary>
    /// Take a binding back off this Inquiry, so the key stops being answerable.
    /// </summary>
    /// <remarks>
    /// The pairing this exists for is <see cref="BindResolve"/>: bind everything, then subtract the handful you
    /// did not mean, for a model where that is shorter than naming the ones you did. Keys are matched without
    /// regard to case, as they are everywhere else, and removing one that was never bound is a no-op rather than
    /// an error, since the state afterwards is the state that was asked for either way.
    /// </remarks>
    /// <param name="key">the key to stop answering, which must not be null or empty</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">the key is null or empty</exception>
    public Inquiry<T> RemoveBinding(string key)
    {
        WeequeryException.ThrowIfNullOrEmpty(key);

        Bindings.Remove(key);
        Collections.Remove(key);

        return this;
    }

    /// <summary>
    /// Add a condition that will be applied to the query when built. Will be AND'ed with any other root conditions
    /// </summary>
    /// <param name="condition"></param>
    /// <returns></returns>
    public Inquiry<T> ApplyCondition(ICondition? condition)
    {
        if (condition is null) { return this; }

        Conditions.Add(condition);

        return this;
    }

    /// <summary>
    /// Parse a query string and add the condition it describes, to be applied when built. Will be AND'ed with any
    /// other root conditions
    /// </summary>
    /// <param name="query">eg. "(Pay &gt; 10000) AND NOT (Name StartsWith 'Temp')"</param>
    /// <param name="style">
    /// <see cref="QueryStyle.Native"/> to accept only the one spelling of each operator, so a caller sending
    /// <c>&amp;&amp;</c> or <c>IS NULL</c> is refused and told what to write. Null, the default, accepts every
    /// spelling, which is what this has always done. See <see cref="ConditionFunctions.ParseQuery"/>
    /// </param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">the query is malformed, see <see cref="ConditionFunctions.ParseQuery"/></exception>
    public Inquiry<T> ApplyCondition(string query, QueryStyle style = QueryStyle.Native)
    {
        var condition = ConditionFunctions.ParseQuery(query, style);
        if (condition is null) { return this; }

        Conditions.Add(condition);

        return this;
    }

    /// <summary>
    /// Add conditions that will be applied to the query when built, if more than one is provided, they will be wrapped in an AND statement
    /// </summary>
    /// <param name="conditions">null, or none, is a NOP. A null element will cause an Exception</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">one of the conditions is null</exception>
    public Inquiry<T> ApplyConditions(IEnumerable<ICondition>? conditions)
    {
        if (conditions is null) { return this; }

        int index = 0;
        foreach (var condition in conditions)
        {
            if (condition is null) { throw new WeequeryException(WeequeryError.ArgumentMissing, $"{nameof(conditions)}[{index}] is null"); }

            Conditions.Add(condition);
            index++;
        }

        return this;
    }

    /// <summary>
    /// Add sort that will be applied to the query when built, sorts will apply in order given
    /// </summary>
    /// <param name="sort"></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException"></exception>
    public Inquiry<T> ApplySort(Sort? sort)
    {
        if (sort is null) { return this; }

        WeequeryException.ThrowIfNullOrEmpty(sort.Field, $"{nameof(sort)}.{nameof(Sort.Field)}");

        Sorts.Add(sort);

        return this;
    }

    /// <summary>
    /// Parse a sort clause and add the sorts it describes, to be applied when built. They apply in the order
    /// written, each breaking ties in the one before.
    /// </summary>
    /// <remarks>
    /// The clause is a comma separated list of fields, each optionally followed by a direction, and may begin
    /// with ORDER BY. See <see cref="Sort.Parse"/> for the whole of it.
    /// <para>
    /// <paramref name="defaultSort"/> is worth supplying wherever the query is paged, since a page of an
    /// unordered query holds arbitrary rows, see <see cref="ApplyPagination"/>.
    /// </para>
    /// </remarks>
    /// <param name="sortString">eg. "Pay DESC, Name". Null, empty or whitespace takes <paramref name="defaultSort"/></param>
    /// <param name="defaultSort">what to sort by when the caller asked for nothing; null, or none, is a NOP</param>
    /// <param name="style">
    /// <see cref="QueryStyle.Native"/> to take only the one word OrderBy prefix, refusing ORDER BY. Null, the
    /// default, takes both. See <see cref="Sort.Parse"/>
    /// </param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">the clause is malformed, see <see cref="Sort.Parse"/></exception>
    public Inquiry<T> ApplySorts(string? sortString, IEnumerable<Sort>? defaultSort = null, QueryStyle? style = null)
    {
        return ApplySorts(Sort.Parse(sortString, defaultSort, style));
    }

    /// <summary>
    /// Add sorts that will be applied to the query when built, sorts will apply in order given
    /// </summary>
    /// <param name="sorts">null, or none, is a NOP. A null element will cause an Exception</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">
    /// one of the sorts is null, or names no field.</exception>
    public Inquiry<T> ApplySorts(IEnumerable<Sort>? sorts)
    {
        if (sorts is null) { return this; }

        int index = 0;
        foreach (var sort in sorts)
        {
            if (sort is null) { throw new WeequeryException(WeequeryError.ArgumentMissing, $"{nameof(sorts)}[{index}] is null"); }

            WeequeryException.ThrowIfNullOrEmpty(sort.Field, $"{nameof(sorts)}[{index}].{nameof(Sort.Field)}");

            Sorts.Add(sort);
            index++;
        }

        return this;
    }

    /// <summary>
    /// Apply paging that will be applied to the query when built
    /// </summary>
    /// <remarks>
    /// <para>
    /// Paging without a unique sort applied will yield undefined output
    /// </para>
    /// </remarks>
    /// <param name="pageSize">rows per page, must be &gt; 0</param>
    /// <param name="page">zero based page index, must be &gt;= 0</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">
    /// either argument is out of range, or combining would cause an integer overflow
    /// </exception>
    public Inquiry<T> ApplyPagination(int pageSize, int page)
    {
        if (pageSize <= 0) { throw new WeequeryException(WeequeryError.ArgumentInvalid, $"{nameof(pageSize)} must be > 0"); }
        if (page < 0) { throw new WeequeryException(WeequeryError.ArgumentInvalid, $"{nameof(page)} must be >= 0"); }

        long skip = (long)pageSize * page;
        if (skip > int.MaxValue)
        {
            throw new WeequeryException(WeequeryError.ArgumentInvalid, $"{nameof(pageSize)} {pageSize} * {nameof(page)} {page} exceeds {int.MaxValue}");
        }

        PageSize = pageSize;
        Page = page;

        return this;
    }

    /// <summary>
    /// Drop the parts of a query that name a field nothing bound, rather than refusing the whole query.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Off by default, and worth leaving off unless you have the problem it solves.</b> That problem is the
    /// stale saved filter: a caller stored a query months ago, a binding has since been renamed or taken away,
    /// and refusing the whole thing means they cannot open their own saved view to fix it. This lets the parts
    /// that still resolve run, and quietly forgets the rest.
    /// </para>
    /// <code>
    /// .IgnoreUnboundFields()
    /// .ApplyCondition("IsActive = true AND Gizmo = 3")   // Gizmo went away, so this filters on IsActive alone
    /// </code>
    /// <para>
    /// <b>Dropping always widens.</b> A test that is not there does not constrain, so a condition made entirely
    /// of unbound fields prunes to nothing and the query returns <i>every row</i>. That is the whole hazard, and
    /// it is why this is opt in: a filter that silently stops filtering is worse than one that refuses out loud,
    /// unless you already decided otherwise. Where the rows are not all the caller's to see, put the constraint
    /// that says so on the <see cref="IQueryable"/> before Weequery ever gets it, or bind it as a constant and
    /// AND it in yourself, rather than trusting a caller's filter to carry it.
    /// </para>
    /// <para>
    /// <b>Only genuinely unbound fields go.</b> A field that is bound but does not grant the use being asked of
    /// it, see <see cref="BindingUse"/>, is a deliberate statement about what a caller may do, and quietly
    /// ignoring one would undo the point of making it. Those are still refused. So is everything else that is
    /// wrong with a query: a malformed string, an operator that does not fit the property, a value that will not
    /// parse, a sort on something with no ordering.
    /// </para>
    /// <para>
    /// It reaches all three halves of a query, and the risk is not the same in each. A dropped <b>sort</b> only
    /// changes the order rows come back in. A dropped <b>projected field</b> only leaves a key out of the row,
    /// though a projection whose every field went reads back as a row of no columns rather than as all of them.
    /// A dropped <b>condition</b> changes which rows there are, which is the one to think about.
    /// </para>
    /// <para>
    /// Inside a quantifier the collection's own allow-list decides, see <see cref="BindCollection"/>: an unbound
    /// field inside the brackets drops from the inner condition, and a quantifier left with no test at all drops
    /// entirely, as does one naming a collection nobody bound.
    /// </para>
    /// </remarks>
    /// <param name="ignore">false to go back to refusing, for the caller deciding this per request</param>
    /// <returns></returns>
    public Inquiry<T> IgnoreUnboundFields(bool ignore = true)
    {
        DropsUnboundFields = ignore;

        return this;
    }

    /// <summary>
    /// If a key is one the bindings hold, which is the test for keeping a field rather than dropping it.
    /// Indexes are split off first, since what has to be bound is the collection.
    /// </summary>
    private bool IsBound(string field)
    {
        var (key, _) = BindingLookup.SplitIndex(field);

        return Bindings.ContainsKey(key) || Collections.ContainsKey(key);
    }

    /// <summary>
    /// If a field survives, noting it as dropped where it does not. For the two halves that filter a flat
    /// list rather than rewriting a tree, see <see cref="DroppedFields"/>.
    /// </summary>
    /// <param name="field">the key as the query named it</param>
    /// <param name="from">which part of the query is asking</param>
    private bool Keep(string field, BindingUse from)
    {
        if (IsBound(field)) { return true; }

        Drop(field, from);

        return false;
    }

    /// <summary>
    /// Read back only the fields named, rather than the whole entity. See <see cref="BuildProjected"/>, which is
    /// what applies this; <see cref="Build"/> ignores it and hands back entities as it always has.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <code>
    /// var rows = query.WithWeequery()
    ///     .BindProperties(MinionBindings)
    ///     .ApplyCondition("IsActive = true")
    ///     .ApplyProjection("Name, Pay")
    ///     .BuildProjected();
    /// </code>
    /// </para>
    /// <para>
    /// <b>The allow-list is the same one.</b> Anything bound may be projected, under the same keys and the same
    /// case-insensitive matching, and a field no binding claimed is refused exactly as it is in a condition. So
    /// this grants nothing filtering did not already, and there is nothing extra to declare.
    /// </para>
    /// <para>
    /// Called more than once, the last call wins, as it does for <see cref="ApplyPagination"/> and unlike
    /// <see cref="ApplyCondition(ICondition?)"/>. A projection is one list of columns rather than something that
    /// accumulates, and two calls asking for different columns can only mean the second changed its mind.
    /// </para>
    /// </remarks>
    /// <param name="fields">
    /// a comma separated list of keys, each written as a condition writes a field and each able to carry an
    /// index: "Name, Pay, Tallies[apples]". Null, empty or whitespace clears any projection already applied.
    /// </param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">the list is malformed</exception>
    public Inquiry<T> ApplyProjection(string? fields)
    {
        return ApplyProjection(Projection.Parse(fields));
    }

    /// <summary>
    /// Read back only the fields named, from keys already in hand rather than from a string.
    /// </summary>
    /// <param name="keys">null or empty clears any projection already applied; a key named twice is kept once</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">a key is null or empty</exception>
    public Inquiry<T> ApplyProjection(IEnumerable<string>? keys)
    {
        return ApplyProjection(Projection.Of(keys));
    }

    /// <summary>
    /// Read back only the fields named, from a projection already read or built.
    /// </summary>
    /// <param name="projection">null or <see cref="Projection.None"/> clears any projection already applied</param>
    /// <returns></returns>
    public Inquiry<T> ApplyProjection(Projection? projection)
    {
        Projected = projection ?? Projection.None;

        return this;
    }

    /// <summary>
    /// The predicate for one condition, bounded where it is this process that will run it.
    /// </summary>
    /// <remarks>
    /// Only <see cref="Operator.IsMatch"/> cares, and only because the overload carrying a timeout is not one a
    /// provider translates, see <see cref="RegexTimeout"/>. A query over an in-memory sequence is evaluated here,
    /// so the bound applies; one over a provider is the database's to run under its own limits.
    /// </remarks>
    /// <param name="condition"></param>
    /// <returns></returns>
    private Expression<Func<T, bool>> Predicate(ICondition condition)
    {
        var predicate = ExpressionBuilder.BuildExpression(Bindings, condition, Collections);

        // LINQ to Objects, which is what AsQueryable over a list gives. Anything else is a provider that will be
        // handed the expression rather than running it here.
        return (Query.Provider is EnumerableQuery) ? RegexTimeout.Apply(predicate) : predicate;
    }

    /// <summary>
    /// The wrapped IQueryable with every condition applied, and nothing else.
    /// </summary>
    /// <remarks>
    /// The rows the caller's filter matched, before any ordering is imposed or any window taken of them. This is
    /// what <see cref="PagedQuery{T}.Matches"/> hands back to be counted.
    /// </remarks>
    /// <returns></returns>
    private IQueryable<T> Filtered()
    {
        var condition = Combined();

        return (condition is null) ? Query : Query.Where(Predicate(condition));
    }

    /// <summary>
    /// Every applied condition as one, pruned where the caller asked for that.
    /// </summary>
    /// <remarks>
    /// Null both for the query that was never given a condition and for the one whose condition was entirely
    /// unbound and pruned away, see <see cref="IgnoreUnboundFields"/>. The two arrive at the same place, which is
    /// a query that filters nothing, and that is exactly the thing to have read the remarks there about.
    /// </remarks>
    /// <returns>null where there is nothing left to filter by</returns>
    private ICondition? Combined()
    {
        ICondition? combined = Conditions.Count switch
        {
            0 => null,

            1 => Conditions.First(),

            // More than one root condition was applied, so they are ANDed, which is what applying a second one means
            _ => new ConjunctionCondition(Operator.And, Conditions),
        };

        if ((combined is null) || (!DropsUnboundFields)) { return combined; }

        return ConditionPruner.Prune(combined, Bindings, Collections, field => Drop(field, BindingUse.Condition));
    }

    /// <summary>
    /// The query with every sort applied, in the order they were given, each breaking ties in the one before.
    /// </summary>
    /// <param name="query"></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">
    /// a sort names a field no binding claimed, a constant, or something with no ordering of its own
    /// </exception>
    private IQueryable<T> Sorted(IQueryable<T> query)
    {
        // A sort on a field nothing bound is dropped where the caller asked for that: it changes the order rows
        // come back in and nothing else, which makes it the safest of the three to forget, see IgnoreUnboundFields
        var sorts = DropsUnboundFields ? Sorts.Where(sort => Keep(sort.Field, BindingUse.Sort)) : Sorts;

        // Once the query has been sorted once, subsequent sorts must chain with ThenBy rather than restart with OrderBy
        bool alreadySorted = false;
        foreach (var sort in sorts)
        {
            var binding = BindingLookup.Resolve(Bindings, sort.Field);

            // The same for every row, so there is nothing here to put in order. Asked before the use, since being
            // a constant is the more particular thing to say and both would be true of one.
            if (binding.IsConstant)
            {
                throw new WeequeryException(WeequeryError.OperatorUnsupported, $"Cannot sort on '{sort.Field}', it is a constant");
            }

            // Bound, but not for ordering by
            if (!binding.Allows(BindingUse.Sort))
            {
                throw new WeequeryException(WeequeryError.OperatorUnsupported, $"Cannot sort on '{sort.Field}': it is bound for {binding.Use}");
            }

            // Refused here rather than left to the comparer
            if (!binding.IsOrderable)
            {
                throw new WeequeryException(WeequeryError.OperatorUnsupported, $"Cannot sort on '{sort.Field}', {binding.PropertyType.Name} has no ordering");
            }

            // Sort on the accessor's own type, not the unwrapped one, otherwise a Nullable<> property cannot
            // satisfy the Func<T, TKey> the sort methods want. Nullable<> keys sort fine, nulls first.
            Type keyType = binding.PropertyType;
            Expression key = binding.Accessor;

            if (binding.RequiresLinkCheck)
            {
                // The path steps through something that may not be there, so reading the key is only safe behind
                // the same guard a comparison gets. A row with a missing link has no key, which is a null, so a
                // value typed key has to be widened to hold one. Those rows sort first, as nulls do.
                keyType = ((keyType.IsValueType) && (!binding.PropertyIsWrappedByNullable)) ? typeof(Nullable<>).MakeGenericType(keyType) : keyType;

                Expression found = (keyType == binding.PropertyType) ? binding.Accessor : Expression.Convert(binding.Accessor, keyType);

                key = Expression.Condition(binding.LinkNotNullCheck, found, Expression.Constant(null, keyType));
            }

            var clause = SortMethods.For(sort.Direction, alreadySorted, typeof(T), keyType);

            // turn the binding accessor into something usable for the call
            var selector = Expression.Lambda(clause.SelectorType, key, SharedBindingParameter);

            // Add the call to the query's own expression and let the provider make a query of it, which is what
            // Queryable.OrderBy does with the arguments it is handed. Doing it here rather than calling that
            // through reflection is the same tree by the time the provider sees it, without the invoke.
            query = query.Provider.CreateQuery<T>(Expression.Call(null, clause.Method, query.Expression, Expression.Quote(selector)));

            alreadySorted = true;
        }

        return query;
    }

    /// <summary>
    /// The query narrowed to the requested page, or as it stands where no paging was asked for.
    /// </summary>
    /// <param name="query"></param>
    /// <returns></returns>
    private IQueryable<T> Windowed(IQueryable<T> query)
    {
        return (PageSize > 0) ? query.Skip(PageSize * Page).Take(PageSize) : query;
    }

    /// <summary>
    /// Apply all conditions, sorts, paging, etc to the wrapped IQueryable and return it
    /// </summary>
    /// <returns></returns>
    public IQueryable<T> Build()
    {
        // Each build describes its own query, so what the last one dropped is not carried into this one
        Dropped.Clear();

        return Windowed(Sorted(Filtered()));
    }

    /// <summary>
    /// Apply everything as <see cref="Build"/> does, and hand back that query together with the one that counts
    /// what the page is a page of.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For the caller that has to answer "showing 21 to 40 of 387". The 387 is not something a page can be asked
    /// for — it is the size of the filtered set the window was taken from — so it is a second query over the same
    /// conditions, and this builds it alongside the first.
    /// <code>
    /// var (page, matches) = query.WithWeequery()
    ///     .BindProperties(MinionBindings)
    ///     .ApplyCondition(request.Filter)
    ///     .ApplySorts(request.Sort, DefaultSort)
    ///     .ApplyPagination(request.PageSize, request.Page)
    ///     .BuildPaged();
    ///
    /// var total = await matches.CountAsync();
    /// var rows  = await page.ToListAsync();
    /// </code>
    /// </para>
    /// <para>
    /// <b>Neither query has run.</b> Counting is left to the caller rather than done here, for two reasons. It is
    /// a database round trip, and the method that makes it without blocking a thread is
    /// <c>CountAsync</c>, which belongs to Entity Framework Core and not to this library — Weequery takes no
    /// dependency on whatever is going to execute the query, and doing the count for you would mean either
    /// taking one or calling the synchronous <c>Count</c> in code that ought to be awaiting. It also stays true
    /// to what <see cref="Build"/> promises, which is a query and no execution, so both halves compose with
    /// whatever else you had planned.
    /// </para>
    /// <para>
    /// Count <see cref="PagedQuery{T}.Matches"/> and not <see cref="PagedQuery{T}.Page"/>: the page is windowed,
    /// so counting it gives the size of the page, which you already know.
    /// </para>
    /// <para>
    /// Where <see cref="ApplyPagination"/> was never called there is no window, the page is the whole filtered
    /// result, and the count agrees with its length. That is not an error, but it is a round trip asking a
    /// question the rows already answer.
    /// </para>
    /// </remarks>
    /// <returns>the page, and the query counting everything the conditions matched; never null, neither half null</returns>
    /// <exception cref="WeequeryException">
    /// whatever <see cref="Build"/> would throw, and at the same point: the conditions and sorts are resolved
    /// against the bindings here, not when either query is enumerated
    /// </exception>
    public PagedQuery<T> BuildPaged()
    {
        // Each build describes its own query, so what the last one dropped is not carried into this one
        Dropped.Clear();

        // Shared, so the count is over exactly the rows the page was taken from and cannot drift from it
        var matches = Filtered();

        return new PagedQuery<T>(Windowed(Sorted(matches)), matches);
    }

    /// <summary>
    /// Apply everything as <see cref="Build"/> does, and read back only the projected fields rather than whole
    /// entities. Each row is a dictionary keyed by binding key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <code>
    /// var rows = query.WithWeequery()
    ///     .BindProperties(MinionBindings)
    ///     .ApplyCondition("IsActive = true")
    ///     .ApplyProjection("Name, Pay")
    ///     .BuildProjected()
    ///     .ToList();
    ///
    /// // [ { "Name": "Alice Fox", "Pay": 12000 }, ... ]
    /// </code>
    /// </para>
    /// <para>
    /// <b>The columns are the ones asked for.</b> Against a database this is a narrower SELECT rather than a
    /// whole row thrown away afterwards, which is the point: three columns of a wide table, over a page of
    /// twenty, is a different amount of work from twenty whole rows. Verified translating on SQLite, PostgreSQL
    /// and SQL Server.
    /// </para>
    /// <para>
    /// <b>Keys come back as the binding spelled them</b>, not as the caller typed them. Fields are matched
    /// without regard to case, so "name" and "NAME" both reach the binding made as "Name", and all of them read
    /// back as "Name". Two callers asking differently get the same shape, which is what anything deserializing it
    /// needs. Entries are added in the order asked for.
    /// </para>
    /// <para>
    /// <b>Values are boxed</b>, so a row is <c>object?</c> whatever the property held, and null where the value
    /// is null or the path to it runs through a null. A field taken at an index nothing sits at is null too,
    /// which is the same rule everywhere else, see <see cref="Operator"/>.
    /// </para>
    /// <para>
    /// With no projection applied this reads every bound field, which is the allow-list's own answer to "all of
    /// it". A constant binding projects its value, the same for every row, see <see cref="BindConstant"/>.
    /// </para>
    /// </remarks>
    /// <returns>the same query <see cref="Build"/> would return, reading dictionaries rather than entities</returns>
    /// <exception cref="WeequeryException">
    /// whatever <see cref="Build"/> would throw, plus a projected field that no binding claimed or that names a
    /// bound collection
    /// </exception>
    public IQueryable<Dictionary<string, object?>> BuildProjected()
    {
        // Build resets what was dropped and records the condition's and the sort's share of it, and the
        // projector adds its own after, so there is nothing to clear here and clearing would lose the first half
        return Build().Select(Projector());
    }

    /// <summary>
    /// The selector for this Inquiry's projection, see <see cref="ProjectionBuilder{T}"/>.
    /// </summary>
    /// <remarks>
    /// The builder is handed the drop test only where the caller asked for one, so it does not have to know what
    /// <see cref="IgnoreUnboundFields"/> is, only if a field survives.
    /// </remarks>
    private Expression<Func<T, Dictionary<string, object?>>> Projector()
    {
        return ProjectionBuilder<T>.Build(Bindings, Collections, Projected, SharedBindingParameter,
            DropsUnboundFields ? (field => Keep(field, BindingUse.Projection)) : null);
    }

    /// <summary>
    /// Apply everything as <see cref="BuildPaged"/> does, and read back only the projected fields.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two halves a grid needs, narrowed to the columns it draws.
    /// <code>
    /// var (page, matches) = query.WithWeequery()
    ///     .BindProperties(MinionBindings)
    ///     .ApplyCondition(request.Filter)
    ///     .ApplySorts(request.Sort, DefaultSort)
    ///     .ApplyPagination(request.PageSize, request.Page)
    ///     .ApplyProjection(request.Fields)
    ///     .BuildPagedProjected();
    ///
    /// var total = await matches.CountAsync();
    /// var rows  = await page.ToListAsync();
    /// </code>
    /// </para>
    /// <para>
    /// <b>Only the page is projected.</b> The count is over rows rather than over what is read off them, so
    /// narrowing it would change nothing about the number and only give a provider more to think about. Counting
    /// <see cref="PagedQuery{T}.Matches"/> gives the same total it would without a projection, which is what it
    /// should: the projection decides what a row says, not which rows there are.
    /// </para>
    /// </remarks>
    /// <returns>the projected page, and the query counting everything the conditions matched</returns>
    /// <exception cref="WeequeryException">whatever <see cref="BuildProjected"/> would throw</exception>
    public PagedQuery<Dictionary<string, object?>> BuildPagedProjected()
    {
        // Each build describes its own query, so what the last one dropped is not carried into this one
        Dropped.Clear();

        // Shared, so the count is over exactly the rows the page was taken from and cannot drift from it
        var matches = Filtered();

        return new PagedQuery<Dictionary<string, object?>>(
            Windowed(Sorted(matches)).Select(Projector()),
            matches.Select(Projector()));
    }

    /// <summary>
    /// Build the predicate for a condition without needing an IQueryable, for use with Where, Any and friends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Where the resulting expression is evaluated changes what the substring operators match. Handed to EF Core
    /// it becomes SQL and the column's collation applies; run against an in-memory collection it uses the
    /// framework's string methods, where StartsWith and EndsWith are culture sensitive and Contains is ordinal.
    /// See the remarks on <see cref="Operator"/> for the detail and for how to get agreement between the two.
    /// </para>
    /// <para>
    /// Every predicate built for one entity type is built over the same parameter, which is what lets the bindings
    /// be resolved once and reused. Independent predicates do not care, but a predicate from here nested inside
    /// another over the same type — a predicate over Minion used inside "minion =&gt; minion.Peers.Any(...)", say —
    /// would have the inner parameter shadow the outer, so the inner test would read the inner element. Build the
    /// outer lambda by hand around this one, rather than combining two of these.
    /// </para>
    /// </remarks>
    /// <param name="bindingRequests"></param>
    /// <param name="condition"></param>
    /// <returns></returns>
    public static Expression<Func<T, bool>> BuildExpression(IEnumerable<BindingRequest> bindingRequests, ICondition condition)
    {
        WeequeryException.ThrowIfNull(bindingRequests);
        WeequeryException.ThrowIfNull(condition);

        return ExpressionBuilder.BuildExpression(BindingSetCache<T>.For(bindingRequests, SharedBindingParameter), condition);
    }

    /// <summary>
    /// Compile a condition to a plain delegate, for filtering objects already in memory.
    /// </summary>
    /// <remarks>
    /// This is always in-memory evaluation, so the substring operators follow the framework's string comparison
    /// rules rather than any database collation: StartsWith and EndsWith are culture sensitive, against
    /// CultureInfo.CurrentCulture, while Contains is ordinal. A condition run through here can therefore match a
    /// different set of items than the same condition run against a database. See the remarks on
    /// <see cref="Operator"/>.
    /// <para>
    /// Because there is no provider here, two things are settled that <see cref="BuildExpression"/> has to leave
    /// open: an IsMatch is bounded by <see cref="MatchTimeout"/>, and the values are written in as constants
    /// rather than read out of the boxes that exist to become query parameters, see <see cref="ValueInliner"/>.
    /// The predicate selects exactly what the uncompiled expression selects; it is cheaper to compile and to run.
    /// </para>
    /// </remarks>
    /// <param name="bindingRequests"></param>
    /// <param name="condition"></param>
    /// <returns></returns>
    public static Func<T, bool> BuildDelegate(IEnumerable<BindingRequest> bindingRequests, ICondition condition)
    {
        // Nothing is going to translate this one, so an IsMatch in it is bounded by MatchTimeout and the values
        // need not stay reachable as parameters. Inlining last, so anything the bounding put in is covered too.
        return ValueInliner.Apply(RegexTimeout.Apply(BuildExpression(bindingRequests, condition))).Compile();
    }
}