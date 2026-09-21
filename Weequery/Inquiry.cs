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
/// <b>An Inquiry is immutable.</b> Every Apply and every Bind leaves the one it was called on exactly as it was
/// and hands back a new one carrying the change, so a fluent chain is a pipeline of values rather than a
/// sequence of modifications. That is how <see cref="IQueryable{T}"/> behaves, and the chain looks enough like a
/// LINQ chain that it had better.
/// </para>
/// <para>
/// Which means a configured one can be kept and branched, and neither branch can reach the other:
/// <code>
/// var bound = query.WithWeequery().BindProperties(MinionBindings);
///
/// var active = bound.ApplyCondition("IsActive = true").Build();
/// var paid   = bound.ApplyCondition("Pay &gt; 10000").Build();    // paid, and only paid
/// </code>
/// Nothing accumulates across the two, because there is nothing they share to accumulate in.
/// </para>
/// <para>
/// <b>What is copied is the lists, not what is in them.</b> A binding is immutable once built and is shared
/// rather than rebuilt, so a copy costs a dictionary and two lists and no more. A condition is yours and is
/// shared as you handed it over, so one you go on to mutate is mutated for every query holding it.
/// </para>
/// <para>
/// The one thing written after construction is <see cref="DroppedFields"/>, which reports on the build that
/// filled it rather than forming part of the configuration, see <see cref="Build"/>.
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

    /// <summary>
    /// What <see cref="PageSize"/> holds where no size has been named, so that
    /// <see cref="InquirySettings.DefaultPageSize"/> is the one that decides. Not zero, which would read as a
    /// page holding nothing.
    /// </summary>
    private const int NoPageSize = -1;

    private int PageSize { get; set; } = NoPageSize;
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

    /// <summary>
    /// What this query decides for itself, see <see cref="InquirySettings"/>. Never null:
    /// <see cref="InquirySettings.Default"/> where the caller gave none.
    /// </summary>
    /// <remarks>
    /// Per query rather than per process, unlike <see cref="MatchTimeout"/>, because the rules a comparison
    /// follows are part of what the caller is asking rather than a bound on what it may cost.
    /// </remarks>
    public InquirySettings Settings { get; init; } = InquirySettings.Default;

    internal Inquiry(IQueryable<T> query)
    {
        Query = query;
    }

    /// <summary>
    /// This Inquiry's configuration on a new one, which is what every Apply and every Bind hands back rather
    /// than changing the one it was called on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What is copied is the lists, not what is in them.</b> A binding is immutable once built and is shared
    /// rather than rebuilt, which is what makes this cheap enough to do on every call: adding a binding to the
    /// copy leaves the original's set alone, and the bindings both of them already had are the same objects. The
    /// same goes for the conditions, which are yours and are shared as you handed them over; a condition you go
    /// on to mutate is mutated for both, as it would be for two queries you built without this.
    /// </para>
    /// <para>
    /// <see cref="DroppedFields"/> is not copied. It reports on a build rather than describing a configuration,
    /// and the copy has not built anything.
    /// </para>
    /// </remarks>
    /// <returns>a new Inquiry over the same query, configured the same way, sharing nothing mutable</returns>
    private Inquiry<T> Copy()
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
            Settings = Settings,
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
        var next = Copy();

        Binding<T>.Create(SharedBindingParameter, selector, next.Bindings, key, use, convert);

        return next.RefuseDuplicateKeys();
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
        var next = Copy();

        Binding<T>.Create(SharedBindingParameter, selector, segments, next.Bindings, key, use, convert);

        return next.RefuseDuplicateKeys();
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

        var next = Copy();

        next.Collections[key] = new CollectionBinding<T, TElement>(key, collection, inner.Bindings);

        return next;
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
    /// Nothing needs putting back when this throws. It runs on the copy the binding call is assembling, so the
    /// Inquiry the caller still holds was never touched by the call at all, and the half built one goes nowhere.
    /// </para>
    /// </remarks>
    /// <returns>the copy it was called on, so it can be returned from the binding call</returns>
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

        var next = Copy();

        Binding<T>.CreateConstant(SharedBindingParameter, key, value, next.Bindings, use, convert);

        return next.RefuseDuplicateKeys();
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

        var next = Copy();

        Binding<T>.Create(SharedBindingParameter, path, next.Bindings, key, use, convert);

        return next.RefuseDuplicateKeys();
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
        var next = Copy();

        foreach (var binding in BindingSetCache<T>.For(bindingRequests, SharedBindingParameter))
        {
            if (next.Bindings.TryGetValue(binding.Key, out var existing))
            {
                // The rule AddTo follows, so a duplicate is answered the same way whichever route it arrives by
                if (!Binding<T>.IsSameBinding(existing, binding.Value)) { throw new WeequeryException(WeequeryError.KeyTaken, $"Binding already exists for '{binding.Key}'"); }

                continue;
            }

            next.Bindings[binding.Key] = binding.Value;
        }

        return next.RefuseDuplicateKeys();
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

        return BindProperties(reqs);
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

        var next = Copy();

        next.Bindings.Remove(key);
        next.Collections.Remove(key);

        return next;
    }

    /// <summary>
    /// Add a condition that will be applied to the query when built. Will be AND'ed with any other root conditions
    /// </summary>
    /// <param name="condition"></param>
    /// <returns></returns>
    public Inquiry<T> ApplyCondition(ICondition? condition)
    {
        if (condition is null) { return this; }

        var next = Copy();

        next.Conditions.Add(condition);

        return next;
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

        var next = Copy();

        next.Conditions.Add(condition);

        return next;
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

        var next = Copy();

        int index = 0;
        foreach (var condition in conditions)
        {
            if (condition is null) { throw new WeequeryException(WeequeryError.ArgumentMissing, $"{nameof(conditions)}[{index}] is null"); }

            next.Conditions.Add(condition);
            index++;
        }

        return next;
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

        var next = Copy();

        next.Sorts.Add(sort);

        return next;
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

        var next = Copy();

        int index = 0;
        foreach (var sort in sorts)
        {
            if (sort is null) { throw new WeequeryException(WeequeryError.ArgumentMissing, $"{nameof(sorts)}[{index}] is null"); }

            WeequeryException.ThrowIfNullOrEmpty(sort.Field, $"{nameof(sorts)}[{index}].{nameof(Sort.Field)}");

            next.Sorts.Add(sort);
            index++;
        }

        return next;
    }

    /// <summary>
    /// Apply paging that will be applied to the query when built
    /// </summary>
    /// <remarks>
    /// <para>
    /// Paging without a unique sort applied will yield undefined output
    /// </para>
    /// <para>
    /// <b>A size that could not hold a page is read as no size at all</b>, so null, zero and a negative all mean
    /// the same thing here: take <see cref="InquirySettings.DefaultPageSize"/>, and where there is no default,
    /// take no window. This is the half of the call that is usually caller input, arriving off a query string
    /// where a field left out and a field left at zero are the same accident, and a library that answered one
    /// with a page and the other with a refusal would be drawing a line the caller never knew was there.
    /// </para>
    /// <para>
    /// <b>And a page behind the first one is the first one.</b> Nothing sits back there to be asked for, so a
    /// negative index is clamped rather than refused, for the same reason and from the same direction: it is a
    /// number off a form, and the answer a person wants for it is the front of the list.
    /// </para>
    /// <para>
    /// Which leaves one thing this still refuses, and it is not a value, it is a pair. A size and a page that
    /// multiply past <see cref="int.MaxValue"/> name a row that cannot be counted to, and there is no nearby
    /// answer to fold that into: the first page is not what was asked for, and the last page is not knowable
    /// without running the query. So it is refused, and it is the only way out of here that is not a query.
    /// </para>
    /// </remarks>
    /// <param name="pageSize">
    /// rows per page. <b>Anything that could not hold a page — null, zero, a negative — is no size at all</b>,
    /// and <see cref="InquirySettings.DefaultPageSize"/> decides instead; where no default was set either, that
    /// is no window and the page index is moot. See the remarks
    /// </param>
    /// <param name="page">
    /// zero based page index. <b>A negative one is the first page</b>, there being nothing behind it to ask for
    /// </param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">
    /// <paramref name="page"/> and a size the caller named combine past <see cref="int.MaxValue"/> rows to skip
    /// </exception>
    public Inquiry<T> ApplyPagination(int? pageSize, int page)
    {
        // Neither is refused, because what arrives here is usually a caller's query string, where a field left
        // out and a field left at nonsense are the same accident and answering one with a page and the other
        // with a 400 is a distinction nobody asked for. A size that could not hold a page is no size, and the
        // default decides; a page behind the first one is the first one
        int size = ((pageSize is int named) && (named > 0)) ? named : NoPageSize;
        int index = (page > 0) ? page : 0;

        // The one pair with no nearby answer to fold into. Only checkable here where the size is one the caller
        // named: where it is coming from the settings the pair is not known until the query is built, so
        // Windowed makes the same check again on what it resolves
        if ((size > 0) && ((long)size * index > int.MaxValue))
        {
            throw new WeequeryException(WeequeryError.ArgumentInvalid, $"{nameof(pageSize)} {size} * {nameof(page)} {index} exceeds {int.MaxValue}");
        }

        var next = Copy();

        next.PageSize = size;
        next.Page = index;

        return next;
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
        var next = Copy();

        next.DropsUnboundFields = ignore;

        return next;
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
        var next = Copy();

        next.Projected = projection ?? Projection.None;

        return next;
    }

    /// <summary>
    /// Apply a whole request at once: its condition, its sorts, the fields it wants read back and the page of
    /// them it asked for. See <see cref="QueryRequest"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The four calls a request handler was going to make anyway, in the order they have to happen, over a type
    /// that binds straight off a query string.
    /// <code>
    /// var (page, matches) = _context.Minions
    ///     .WithWeequery()
    ///     .BindProperties(MinionBindings)
    ///     .ApplyRequest(request, DefaultSort)
    ///     .BuildPagedProjected();
    /// </code>
    /// </para>
    /// <para>
    /// <b>A member the request did not name is a member it is saying nothing about</b>, and what that means is
    /// whatever it already meant. No condition adds none, and a query that had one keeps it, conditions being
    /// the one thing here that accumulates. No sorts takes <paramref name="defaultSort"/>. No page size takes
    /// <see cref="InquirySettings.DefaultPageSize"/>. <b>No fields clears any projection already applied</b>,
    /// which is the odd one out and is so because a projection is one list rather than something that
    /// accumulates, see <see cref="ApplyProjection(Projection?)"/>: the request is the caller saying which
    /// columns they want, and naming none of them means all of the ones they may have.
    /// </para>
    /// <para>
    /// <b>It refuses as its parts refuse.</b> Malformed text throws where the corresponding Apply would have
    /// thrown, and the first fault wins, so a request with a bad filter and a bad sort reports the filter.
    /// <see cref="Validate(QueryRequest, IEnumerable{Sort}?, QueryStyle)"/> is the one that reports all of them
    /// and throws none, and is worth asking first wherever the request came from outside.
    /// </para>
    /// </remarks>
    /// <param name="request">the caller's query; null is a NOP</param>
    /// <param name="defaultSort">
    /// what to sort by where the request named no sorts, which is worth supplying wherever the query is paged,
    /// since a page of an unordered query holds arbitrary rows. See <see cref="ApplyPagination"/>
    /// </param>
    /// <param name="style">
    /// how strictly to read the text halves. <see cref="QueryStyle.Native"/>, the default, accepts one spelling
    /// per operator, see <see cref="QueryStyle"/>
    /// </param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">
    /// any half of the request is malformed, or its paging is out of range
    /// </exception>
    public Inquiry<T> ApplyRequest(QueryRequest? request, IEnumerable<Sort>? defaultSort = null, QueryStyle style = QueryStyle.Native)
    {
        if (request is null) { return this; }

        return ApplyCondition(request.UnpackCondition(style))
            .ApplySorts(request.UnpackSorts(defaultSort, style))
            .ApplyProjection(request.UnpackProjection())
            .ApplyPagination(request.PageSize, request.Page ?? 0);
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
        return (Query.Provider is EnumerableQuery) ? StringComparisonRules.Apply(RegexTimeout.Apply(predicate), Settings) : predicate;
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
    /// How many rows a page holds, which is the size the caller named or, where it named none,
    /// <see cref="InquirySettings.DefaultPageSize"/>.
    /// </summary>
    /// <returns>zero where nothing named a size and no default was set, which is the query that has no window</returns>
    private int EffectivePageSize()
    {
        return (PageSize > 0) ? PageSize : (Settings.DefaultPageSize ?? 0);
    }

    /// <summary>
    /// The query narrowed to the requested page, or as it stands where no paging was asked for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Where <see cref="InquirySettings.DefaultPageSize"/> was set, a query that named no size is still windowed,
    /// and a query that named no page is windowed at the first one. That is what having a default means: the
    /// caller omitting the field gets the default rather than getting everything.
    /// </para>
    /// </remarks>
    /// <param name="query"></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">
    /// the resolved size and the page combine past <see cref="int.MaxValue"/> rows to skip.
    /// <see cref="ApplyPagination"/> makes the same check on the pair it was handed; this is the one for the pair
    /// only settled here, where the size came from the settings
    /// </exception>
    private IQueryable<T> Windowed(IQueryable<T> query)
    {
        int pageSize = EffectivePageSize();
        if (pageSize <= 0) { return query; }

        int page = (Page > 0) ? Page : 0;

        long skip = (long)pageSize * page;
        if (skip > int.MaxValue)
        {
            throw new WeequeryException(WeequeryError.ArgumentInvalid, $"page size {pageSize} * page {page} exceeds {int.MaxValue}");
        }

        return query.Skip((int)skip).Take(pageSize);
    }

    /// <summary>
    /// Find out what is wrong with this query without building it, and without throwing. See
    /// <see cref="ValidationResult"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything <see cref="Build"/> would refuse, reported rather than raised, and all of it rather than the
    /// first of it. For the handler answering a caller who filled in a form and got it wrong:
    /// <code>
    /// var problems = inquiry.Validate();
    /// if (!problems.IsValid) { return BadRequest(problems.Problems.Select(problem =&gt; problem.ToString())); }
    /// </code>
    /// </para>
    /// <para>
    /// <b>It looks at all three halves</b>, since a query has three ways to be wrong and a caller fixing them one
    /// round trip at a time is the thing worth avoiding. The condition is resolved against the bindings, then the
    /// sorts, then the projection where one was applied. Each is looked at whatever the ones before it said.
    /// </para>
    /// <para>
    /// <b>Only what is applied is looked at.</b> A projection nobody asked for validates, being the allow-list's
    /// own answer to "all of it"; paging was already held to its range by <see cref="ApplyPagination"/>, where a
    /// bad page is refused as it is written rather than kept to be complained about later. What arrives as text
    /// is the same: <see cref="ApplyCondition(string, QueryStyle)"/> parses when it is called, so a malformed
    /// string has thrown long before this. <see cref="Validate(QueryRequest, IEnumerable{Sort}?, QueryStyle)"/>
    /// is the overload that reads the text too, and is the one for a request straight off the wire.
    /// </para>
    /// <para>
    /// <b>Nothing is executed and nothing is kept.</b> The queries this builds to see whether they can be built
    /// are thrown away, so this costs what a build costs and changes nothing about what a later build does. The
    /// one thing it does leave behind is <see cref="DroppedFields"/>, which it fills exactly as a build fills it,
    /// since what a query quietly drops is worth knowing at the same time as what it refuses outright, see
    /// <see cref="IgnoreUnboundFields"/>.
    /// </para>
    /// <para>
    /// <b>Valid means it will build</b>, which is a narrower claim than it will work. A provider may still refuse
    /// what it is handed — <see cref="Operator.IsMatch"/> against SQL Server is the standing example — and that
    /// is between the provider and the query, some way past here.
    /// </para>
    /// </remarks>
    /// <returns>the problems, in the order the halves are looked at; never null</returns>
    public ValidationResult Validate()
    {
        // As a build does, and for the same reason: what the last one dropped does not belong to this one
        Dropped.Clear();

        List<ValidationProblem> problems = [];

        try
        {
            var condition = Combined();
            if (condition is not null) { Predicate(condition); }
        }
        catch (WeequeryException error) { problems.Add(new ValidationProblem(BindingUse.Condition, error.Error, error.Message)); }

        // Against the unfiltered query, so a condition that refused does not take the sorts down with it
        try { Sorted(Query); }
        catch (WeequeryException error) { problems.Add(new ValidationProblem(BindingUse.Sort, error.Error, error.Message)); }

        // Only where one was asked for. With none applied this reads every projectable binding, which cannot
        // refuse anything the bindings themselves did not already refuse when they were made
        if (!Projected.IsEmpty)
        {
            try { Projector(); }
            catch (WeequeryException error) { problems.Add(new ValidationProblem(BindingUse.Projection, error.Error, error.Message)); }
        }

        return (problems.Count == 0) ? ValidationResult.Valid : new ValidationResult(problems);
    }

    /// <summary>
    /// Find out what is wrong with a request before applying it, including the parts of it that have to be read
    /// before they can be refused. See <see cref="QueryRequest"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one to reach for where the query came from outside. <see cref="ApplyRequest"/> parses as it applies
    /// and throws on the first thing it cannot read, which is the right behaviour for code that has already
    /// decided to run the query and the wrong one for code deciding whether to:
    /// <code>
    /// var problems = inquiry.Validate(request, DefaultSort);
    /// if (!problems.IsValid) { return BadRequest(problems.Problems.Select(problem =&gt; problem.ToString())); }
    ///
    /// var (page, matches) = inquiry.ApplyRequest(request, DefaultSort).BuildPagedProjected();
    /// </code>
    /// </para>
    /// <para>
    /// <b>This Inquiry is not touched.</b> The request is applied to a copy of it, and the copy is what
    /// gets asked, so asking is free of consequence and the query you go on to build is the one you had. Which
    /// also means <see cref="DroppedFields"/> is the copy's rather than this one's, and is gone with it — call
    /// <see cref="Validate()"/> after applying where that list is what you are after.
    /// </para>
    /// <para>
    /// <b>Two passes, so a half can report twice.</b> Reading the text and resolving what it says against the
    /// bindings are separate failures: a sort clause that will not parse is one problem, and a sort clause that
    /// parses and names a field nobody bound is another. Every parse fault is reported first, then everything
    /// <see cref="Validate()"/> finds in what did parse.
    /// </para>
    /// <para>
    /// <b>It validates the request on top of what is already applied</b>, since that is what applying it would
    /// do: a condition this Inquiry already carries is ANDed with the request's, and is validated alongside it.
    /// </para>
    /// </remarks>
    /// <param name="request">the caller's query; null asks about this Inquiry as it stands, see <see cref="Validate()"/></param>
    /// <param name="defaultSort">what to sort by where the request named no sorts, as <see cref="ApplyRequest"/> takes it</param>
    /// <param name="style">how strictly to read the text halves, as <see cref="ApplyRequest"/> takes it</param>
    /// <returns>the problems, parse faults first; never null</returns>
    public ValidationResult Validate(QueryRequest? request, IEnumerable<Sort>? defaultSort = null, QueryStyle style = QueryStyle.Native)
    {
        if (request is null) { return Validate(); }

        List<ValidationProblem> problems = [];
        var candidate = Copy();

        ICondition? condition = null;
        try { condition = request.UnpackCondition(style); }
        catch (WeequeryException error) { problems.Add(new ValidationProblem(BindingUse.Condition, error.Error, error.Message)); }

        List<Sort>? sorts = null;
        try { sorts = request.UnpackSorts(defaultSort, style); }
        catch (WeequeryException error) { problems.Add(new ValidationProblem(BindingUse.Sort, error.Error, error.Message)); }

        Projection? projection = null;
        try { projection = request.UnpackProjection(); }
        catch (WeequeryException error) { problems.Add(new ValidationProblem(BindingUse.Projection, error.Error, error.Message)); }

        // Not one of the three halves, so it is reported against the request itself
        try { candidate = candidate.ApplyPagination(request.PageSize, request.Page ?? 0); }
        catch (WeequeryException error) { problems.Add(new ValidationProblem(BindingUse.None, error.Error, error.Message)); }

        // Whatever did read, so the rest of the request is still held to the bindings and the caller hears about
        // all of it at once. A half that did not read is simply not there to ask about
        candidate = candidate
            .ApplyCondition(condition)
            .ApplySorts(sorts)
            .ApplyProjection(projection);

        problems.AddRange(candidate.Validate().Problems);

        return (problems.Count == 0) ? ValidationResult.Valid : new ValidationResult(problems);
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
    /// This is always in-memory evaluation, so the string comparisons follow the rules the caller asked for
    /// rather than any database collation, see <see cref="InquirySettings.StringComparison"/>. Those default to
    /// <see cref="StringComparison.Ordinal"/>, which is what a database compares by, so a condition run through
    /// here answers as the same condition run against a database does; ask for a culture and it need not. See the
    /// remarks on <see cref="Operator"/>.
    /// <para>
    /// Because there is no provider here, three things are settled that <see cref="BuildExpression"/> has to
    /// leave open: the comparison rules above, an IsMatch is bounded by <see cref="MatchTimeout"/>, and the
    /// values are written in as constants
    /// rather than read out of the boxes that exist to become query parameters, see <see cref="ValueInliner"/>.
    /// The predicate selects exactly what the uncompiled expression selects; it is cheaper to compile and to run.
    /// </para>
    /// </remarks>
    /// <param name="bindingRequests"></param>
    /// <param name="condition"></param>
    /// <param name="settings">[OPT] the rules to compile in, see <see cref="InquirySettings"/>; the defaults where none are given</param>
    /// <returns></returns>
    public static Func<T, bool> BuildDelegate(IEnumerable<BindingRequest> bindingRequests, ICondition condition, InquirySettings? settings = null)
    {
        // Nothing is going to translate this one, so an IsMatch in it is bounded by MatchTimeout, the string
        // comparisons are told how to compare, and the values need not stay reachable as parameters. Inlining
        // last, so anything the other two put in is covered too.
        return ValueInliner.Apply(StringComparisonRules.Apply(RegexTimeout.Apply(BuildExpression(bindingRequests, condition)), settings ?? InquirySettings.Default)).Compile();
    }
}
