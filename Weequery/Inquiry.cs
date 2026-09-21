using System.Diagnostics.CodeAnalysis;
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
/// The bound properties are the allow-list: a condition or a sort naming against a field with no binding is refused 
/// or dropped.
/// </para>
/// <para>
/// Inquiry is Immutable, which means a configured one can be kept and branched, and neither branch can reach the other:
/// <code>
/// var bound = query.WithWeequery().BindProperties(MinionBindings);
/// var active = bound.ApplyCondition("IsActive = true").Build();
/// var paid   = bound.ApplyCondition("Pay &gt; 10000").Build();    // paid, and only paid
/// </code>
/// Nothing accumulates across the two, because there is nothing they share to accumulate in.
/// </para>
/// <para>
/// <b>What *IS* copied is the lists, not what is in them.</b> A binding is immutable once built and is shared
/// rather than rebuilt, so a copy costs a dictionary and two lists and no more.
/// </para>
/// <para>
/// The one outlier to this is <see cref="DroppedFields"/>, which reports on the build that
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
    /// <see cref="BindingSetCache{T}"/>, and costs nothing to do: an expression tree is immutable, and a parameter is an
    /// identity rather than a value, so two lambdas built over the same one are still two independent lambdas.
    /// </para>
    /// </summary>
    private static readonly ParameterExpression SharedBindingParameter = Expression.Parameter(typeof(T));

    private Dictionary<string, Binding<T>> Bindings { get; init; } = BindingLookup.Create<T>();

    /// <summary>
    /// Bound collections, seperate from bound properties
    /// </summary>
    private Dictionary<string, ICollectionBinding<T>> Collections { get; init; } = new(BindingLookup.KeyComparer);

    private List<ICondition> Conditions { get; init; } = new();
    private List<Sort> Sorts { get; init; } = new();

    /// <summary>
    /// Which fields <see cref="BuildProjected"/> reads back, see <see cref="ApplyProjection(string?, QueryStyle)"/>. Default empty will return
    /// every bound field.
    /// </summary>
    private Projection Projected { get; set; } = Projection.None;

    /// <summary>
    /// The projection a caller applied, or <see cref="Projection.None"/> where none was, see
    /// <see cref="ApplyProjection(string?, QueryStyle)"/>.
    /// </summary>
    /// <remarks>
    /// A projection builder will need to query this to determine what to retain.
    /// </remarks>
    public Projection AppliedProjection { get { return Projected; } }

    /// <summary>
    /// What <see cref="PageSize"/> holds where no size has been named, so that
    /// <see cref="InquirySettings.DefaultPageSize"/> is the one that decides. Not zero, which would read as a
    /// page holding nothing.
    /// </summary>
    private const int UnsetPageSize = -1;

    private int PageSize { get; set; } = UnsetPageSize;
    private int Page { get; set; } = -1;

    private List<DroppedField> Dropped { get; init; } = new();

    /// <summary>
    /// What the last build dropped from the query, see <see cref="InquirySettings.IgnoreUnboundFields"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Empty unless dropping was asked for, since nothing is dropped otherwise. Filled in by whichever of
    /// <see cref="Build"/>, <see cref="BuildPaged"/>, <see cref="BuildProjected"/> and
    /// <see cref="BuildPagedProjected"/> was called, so it describes the last built rather than accumulating
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
    /// Mark a field as dropped, if it has not already been noted for the given use
    /// </summary>
    /// <remarks>
    /// We are unlikely to care if it is dropped from the query twice, but we might care if it was dropped from the 
    /// query AND the sort.
    /// </remarks>
    private void Drop(string field, BindingUse from, string reason = DroppedField.Unbound)
    {
        field = BindingLookup.SplitIndex(field).Key; // A index would be meaningless here

        if (Dropped.Any(entry => (entry.From == from) && BindingLookup.KeyComparer.Equals(entry.Field, field))) { return; }

        Dropped.Add(new DroppedField(field, from, reason));
    }

    /// <summary>
    /// Configurable timeout for <see cref="Operator.IsMatch"/>. One second by default; assign to change it, or
    /// <see cref="Regex.InfiniteMatchTimeout"/> to remove the bound.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A value that exceeds it will raise <see cref="RegexMatchTimeoutException"/> from wherever the query is being
    /// enumerated, instead of a <see cref="WeequeryException"/>
    /// </para>
    /// <para>
    /// This bounds the match only when it is run in-memory Weequery runs it; via EF the databased limits will apply
    /// </para>
    /// </remarks>
    [SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Per entity type is the point: the bound is one per T and the builders close over T's bindings, so Inquiry<T> is where a caller already is when it needs them.")]
    public static TimeSpan MatchTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Any evaluation tuning knobs for this query <see cref="InquirySettings"/>. 
    /// </summary>
    public InquirySettings Settings { get; internal set; } = InquirySettings.Default;

    internal Inquiry(IQueryable<T> query)
    {
        Query = query;
    }

    /// <summary>
    /// Return a cloned copy of this Inquiry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What is copied is the lists, not what is in them.</b> A binding is immutable once built and is shared
    /// rather than rebuilt. Adding a binding or condition to the copy leaves the original's set alone.
    /// </para>
    /// </remarks>
    /// <returns>a clone, sharing nothing mutable</returns>
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
    /// cannot write on its own. If a key is not provided, the full path is used.
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
    /// <b>The inside is its own allow-list.</b> Properties bound within the collection are not visible outside it
    /// </para>
    /// <para>
    /// The collection is only bound for the specified tests, it cannot be used for direct comparisions unless it
    /// is also bound with <see cref="BindProperty{TProperty}(Expression{Func{T, TProperty}}, string?, BindingUse, ValueConverter)"/> 
    /// under a different key.
    /// </para>
    /// <para>
    /// How EF Core chooses to interpret this or not is up to the provider. A quantifier becomes Any or All over
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
    /// the key is invalid, is already in use, the property is not a collection, or nothing was bound inside
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

        if (Bindings.ContainsKey(key) || Collections.ContainsKey(key))
        {
            throw new WeequeryException(WeequeryError.KeyTaken, $"Binding already exists for '{key}'");
        }

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
    /// Refuse a property binding if a collection is using the same key
    /// </summary>
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
    /// Bind a constant value rather than a property.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The caller writes "Pay &gt; [Threshold]" and the application decides what
    /// Threshold is, per request, per tenant, or per anything else it knows and the caller does not.
    /// <code>
    /// .BindConstant("Threshold", payThreshold)
    /// .ApplyCondition("Pay &gt; [Threshold]")
    /// </code>
    /// </para>
    /// <para>
    /// As it is a constant value, it cannot be used to sort on
    /// </para>
    /// </remarks>
    /// <typeparam name="TValue"></typeparam>
    /// <param name="key">the name a caller refers to it by</param>
    /// <param name="value">must not be null: a constant stands for a value, so it needs one</param>
    /// <param name="use">[OPT] what the binding may be used for, all three by default, see <see cref="BindingUse"/></param>
    /// <param name="convert">[OPT] a normalisation applied to its value, see <see cref="ValueConverter"/></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException"></exception>
    public Inquiry<T> BindConstant<TValue>(string key, TValue value, BindingUse use = BindingUse.Test | BindingUse.Projection, ValueConverter? convert = null)
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
    /// Bind the properties in the list, if a key for one is not provided, it will be bound as the property path
    /// </summary>
    /// <remarks>
    /// <para>
    /// A set of requests can be resolved once for the process and cached, see <see cref="BindingSetCache{T}"/>
    /// </para>
    /// <para>
    /// A request for a key already bound to the <b>same property</b> is merged rather than refused: the uses are
    /// added together and a converter either side named is kept, so binding the same set twice, or a broad set
    /// and then a narrow one, adds rather than colliding. A key standing for a <i>different</i> property is a
    /// conflict and is refused.
    /// </para>
    /// </remarks>
    /// <param name="bindingRequests"></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">a key points to a different property, or attempts to merge distinct converters</exception>
    public Inquiry<T> BindProperties(IEnumerable<BindingRequest> bindingRequests)
    {
        WeequeryException.ThrowIfNull(bindingRequests);

        var next = Copy();

        foreach (var binding in BindingSetCache<T>.For(bindingRequests, SharedBindingParameter))
        {
            if (next.Bindings.TryGetValue(binding.Key, out var existing))
            {
                if (!Binding<T>.IsSameBinding(existing, binding.Value)) { throw new WeequeryException(WeequeryError.KeyTaken, $"Binding already exists for '{binding.Key}'"); }

                next.Bindings[binding.Key] = Binding<T>.Merged(existing, binding.Value, binding.Key);

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
    /// An allow-list, where the list is <b>ALL OF IT</b> Every property name on the type, and on every type
    /// it reaches, goes on the wire for a caller to read and filter by. Use
    /// <see cref="BindingResolutionSettings"/> to determine what types or paths should be skipped.
    /// </para>
    /// <para>
    /// Paths are distinct by construction, so nothing here collides with anything else here; but can still
    /// collide with a binding already made by hand, see <see cref="BindResolve"/>.
    /// </para>
    /// <para>
    /// A property whose path conflicts with an query keyword is bound with an underscore after it, so a model holding a
    /// property called Contains resolves it as "Contains_" rather than refusing the whole model, see
    /// <see cref="BindingResolver.KeyFor"/>.
    /// </para>
    /// <para>
    /// Things to know:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// It will not descend into a struct, so DateTime.Year is not reached this way and must be bound by
    /// hand, see <see cref="BindProperty{TProperty}(Expression{Func{T, TProperty}}, string[], string?, BindingUse, ValueConverter)"/>. A
    /// collection is descended as the class it is rather than as its element type, and what you get from one is Count and
    /// Capacity, which a provider may well refuse to translate.
    /// </description></item>
    /// <item><description>
    /// A type already above it the path will not be revisited, so Parent.Child.Parent will not resolve
    /// </description></item>
    /// <item><description>
    /// The cycle guard bounds depth, not width.
    /// </description></item>
    /// </list>
    /// </remarks>
    /// <param name="maxDepth">
    /// how many levels below the entity to reach, 0 for immediate properties, 1 for their
    /// properties as well. Defaults to 1; bounded to [0, 16]
    /// </param>
    /// <param name="settings">
    /// [OPT] what to leave out; by default only string properties will not not be collected
    /// </param>
    /// <param name="use">[OPT] what the binding may be used for, everything by default, see <see cref="BindingUse"/></param>
    /// <returns>the requests, in path order, ready for <see cref="BindProperties"/></returns>
    /// <exception cref="WeequeryException">a resolved path does not make a valid key</exception>
    [SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Per entity type is the point: the bound is one per T and the builders close over T's bindings, so Inquiry<T> is where a caller already is when it needs them.")]
    public static IReadOnlyList<BindingRequest> ResolveBindables(int maxDepth = 1, BindingResolutionSettings? settings = null, BindingUse use = BindingUse.All)
    {
        maxDepth = Math.Min(Math.Max(maxDepth, 0), 16); // bound to [0,16]
        settings = (settings is null) ? BindingResolutionSettings.Default : new(settings); // use defaults if nothing provided

        var resolved = BindingResolver.ResolveBindables(new List<BindingRequest>(), typeof(T), 0, maxDepth, "", settings, new HashSet<Type>());

        return (use == BindingUse.All) ? resolved : [.. resolved.Select(request => new BindingRequest(request.PropertyPath, request.Key, use))];
    }

    /// <summary>
    /// Bind every readable property this entity reaches, as <see cref="ResolveBindables(int, BindingResolutionSettings, BindingUse)"/> resolves them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read the warning on <see cref="ResolveBindables(int, BindingResolutionSettings, BindingUse)"/> first.</b> With default settings, this will bind everything.
    /// </para>
    /// <para>
    /// Adds to whatever is already bound rather than replacing it. A key this resolves that a
    /// <see cref="BindProperty(string, string?, BindingUse, ValueConverter)"/> call already claimed for the
    /// <b>same property</b> is merged rather than refused: the uses are added, and a converter either
    /// side named is kept (or both, if the converter is the same instance). Only a key standing for a 
    /// <i>different</i> property is refused, see <see cref="BindProperties"/>.
    /// </para>
    /// </remarks>
    /// <param name="maxDepth">
    /// how many levels below the entity to reach. Defaults to 1, and bounded to [0, 16]
    /// </param>
    /// <param name="settings">
    /// [OPT] what to leave out; null leaves nothing out, but will not bind string properties
    /// </param>
    /// <param name="use">[OPT] what the binding may be used for, everything by default, see <see cref="BindingUse"/></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">a resolved path does not make a valid key, or two bindings claim one key</exception>
    public Inquiry<T> BindResolve(int maxDepth = 1, BindingResolutionSettings? settings = null, BindingUse use = BindingUse.All)
    {
        var reqs = ResolveBindables(maxDepth, settings, use);

        return BindProperties(reqs);
    }

    /// <summary>
    /// Take a binding back off this Inquiry, or narrow the scope of its use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pairing this exists for is <see cref="BindResolve"/>: bind everything, then subtract the handful you
    /// do not want.
    /// </para>
    /// <para>
    /// <b>Naming a use removes the use rather than the whole binding</b>, which is the counterweight to a
    /// binding's use only ever widening, see <see cref="Binding{TClass}.Widened"/>
    /// <code>
    /// .BindResolve()
    /// .RemoveBinding("Pay", BindingUse.Condition | BindingUse.Sort)   // still readable, no longer testable
    /// .RemoveBinding("PasswordHash")                                  // gone entirely
    /// </code>
    /// </para>
    /// <para>
    /// Bound collections are only ever testable, so any Remove against one will remove it entirely
    /// </para>
    /// </remarks>
    /// <param name="key">the binding name</param>
    /// <param name="use">
    /// [OPT] what to stop it being used for, <see cref="BindingUse.All"/> by default
    /// </param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">the key is null or empty</exception>
    public Inquiry<T> RemoveBinding(string key, BindingUse use = BindingUse.All)
    {
        WeequeryException.ThrowIfNullOrEmpty(key);

        var next = Copy();

        if (next.Bindings.TryGetValue(key, out var existing))
        {
            var narrowed = existing.Narrowed(use);

            // Nothing left to answer with, so the binding goes rather than staying as one that refuses everything
            if (narrowed.Use == BindingUse.None) { next.Bindings.Remove(key); }
            else { next.Bindings[key] = narrowed; }
        }

        if (use.HasFlag(BindingUse.Test)) { next.Collections.Remove(key); }

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
    /// <param name="filterString">eg. "(Pay &gt; 10000) AND NOT (Name StartsWith 'Temp')"</param>
    /// <param name="style">
    /// <see cref="QueryStyle.Native"/> to accept only the one spelling of each operator, so a caller sending
    /// <c>&amp;&amp;</c> or <c>IS NULL</c> is refused and told what to write. Null, the default, accepts every
    /// spelling, which is what this has always done. See <see cref="ConditionFunctions.ParseQuery"/>
    /// </param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">the query is malformed, see <see cref="ConditionFunctions.ParseQuery"/></exception>
    public Inquiry<T> ApplyCondition(string filterString, QueryStyle style = QueryStyle.Native)
    {
        var condition = ConditionFunctions.ParseQuery(filterString, style);
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
    /// Parse a sort clause and add the sorts it describes, to be applied when built. They will apply in the order
    /// provided
    /// </summary>
    /// <remarks>
    /// The clause is a comma separated list of fields, each optionally followed by a direction.
    /// See <see cref="Sort.Parse"/> for the format
    /// <para>
    /// <paramref name="defaultSort"/> should be supplied wherever the query is paged, since a page of an
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
    public Inquiry<T> ApplySorts(string? sortString, IEnumerable<Sort>? defaultSort = null, QueryStyle style = QueryStyle.Native)
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
    /// A size and a page that multiply past <see cref="int.MaxValue"/> cannot be resolved, so it is refused.
    /// </para>
    /// </remarks>
    /// <param name="pageSize">
    /// rows per page. A null, or LEQ 0 will be treated as <see cref="InquirySettings.DefaultPageSize"/>; where
    /// no default was set, that is no window and the page index is moot.
    /// </param>
    /// <param name="page">
    /// zero based page index. A negative value will be treated as page 0
    /// </param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">
    /// <paramref name="page"/> and a size the caller named combine past <see cref="int.MaxValue"/> rows to skip
    /// </exception>
    public Inquiry<T> ApplyPagination(int? pageSize, int page)
    {
        // bound page and pageSize
        int size = ((pageSize is int named) && (named > 0)) ? named : UnsetPageSize;
        int index = (page > 0) ? page : 0;

        // Check if the combined values will overflow an int
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
    /// If a key maps to a bound property or collection
    /// </summary>
    private bool IsBound(string field)
    {
        var key = BindingLookup.SplitIndex(field).Key;

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
    /// Read back only the fields named, rather than the whole entity. See <see cref="BuildProjected"/>
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
    /// If called multiple times, the last call wins.
    /// </para>
    /// </remarks>
    /// <param name="projectionString">
    /// a comma separated list of keys, each written as a binding key and each able to carry an
    /// index: "Name, Pay, Tallies[apples]". An empty list clears any projection already applied, a key 
    /// named twice is kept once
    /// </param>
    /// <param name="style"></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">the list is malformed</exception>
    public Inquiry<T> ApplyProjection(string? projectionString, QueryStyle style = QueryStyle.Native)
    {
        return ApplyProjection(Projection.Parse(projectionString, style));
    }

    /// <summary>
    /// Read back only the fields named, rather than the whole entity. See <see cref="BuildProjected"/>
    /// </summary>
    /// <param name="keys">An empty list clears any projection already applied; a key named twice is kept once</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">a key is null or empty</exception>
    public Inquiry<T> ApplyProjection(IEnumerable<string>? keys)
    {
        return ApplyProjection(Projection.Of(keys));
    }

    /// <summary>
    /// Read back only the fields named from a projection already applied
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
    /// var (page, total) = _context.Minions
    ///     .WithWeequery()
    ///     .BindProperties(MinionBindings)
    ///     .ApplyRequest(request, DefaultSort)
    ///     .BuildPagedProjected();
    /// </code>
    /// </para>
    /// <para>
    /// <b>It refuses as its components refuse.</b> Expected errors will be identical to calling the same functions with
    /// the same data. <see cref="Validate(QueryRequest, IEnumerable{Sort}?)"/> will report any the errors 
    /// in the same place and is worth requesting first if the request came from outside.
    /// </para>
    /// </remarks>
    /// <param name="request">the caller's query; null is a NOP</param>
    /// <param name="defaultSort">
    /// what to sort by where the request named no sorts. See <see cref="ApplyPagination"/>
    /// </param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">
    /// any part of the request is malformed, spells something a way <see cref="QueryStyle.Native"/> refuses,
    /// or its paging is out of range
    /// </exception>
    public Inquiry<T> ApplyRequest(QueryRequest? request, IEnumerable<Sort>? defaultSort = null)
    {
        if (request is null) { return this; }

        // Read once rather than a part at a time, so the one string is parsed once, see QueryRequest.Unpack
        var unpacked = request.Unpack(defaultSort);

        return ApplyCondition(unpacked.Condition)
            .ApplySorts(unpacked.Sorts)
            .ApplyProjection(unpacked.Projection)
            .ApplyPagination(request.PageSize, request.Page ?? 0);
    }

    /// <summary>
    /// Refuse a condition using an operator that has been excluded, see
    /// <see cref="InquirySettings.Operators"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Check here because this is where <see cref="Build"/> and <see cref="Validate()"/> meet
    /// </para>
    /// <para>
    /// Every operator counts, including the conjunctions and the ones inside a quantifier, see
    /// <see cref="ConditionFunctions.OperatorsUsed"/>. The first one refused is the one reported, as everywhere
    /// else, see <see cref="ValidationResult"/>.
    /// </para>
    /// <para>
    /// The default set allows everything
    /// </para>
    /// </remarks>
    /// <param name="condition"></param>
    /// <exception cref="WeequeryException">it uses an operator the set does not allow</exception>
    private void RefuseUnsupportedOperators(ICondition condition)
    {
        if (Settings.Operators.IsEverything) { return; }

        if (ConditionFunctions.FirstUnsupported(condition, Settings.Operators) is not { } unsupported) { return; }

        throw new WeequeryException(
            WeequeryError.NotTranslatable,
            string.IsNullOrEmpty(unsupported.Field)
                ? $"{unsupported.Operator} is not supported by this data source"
                : $"'{unsupported.Field}' is tested with {unsupported.Operator}, which this data source does not support");
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
        RefuseUnsupportedOperators(condition);

        var predicate = ExpressionBuilder.BuildExpression(Bindings, condition, Collections);

        // LINQ to Objects, which is what AsQueryable over a list gives. Anything else is a provider that will be
        // handed the expression rather than running it here.
        return (Query.Provider is EnumerableQuery) ? StringComparisonRules.Apply(RegexTimeout.Apply(predicate), Settings) : predicate;
    }

    /// <summary>
    /// The wrapped IQueryable with every condition applied, and nothing else.
    /// </summary>
    /// <remarks>
    /// The rows the caller's filter matched, before any ordering is imposed, or any window applied. This is
    /// what <see cref="PagedQuery{T}.Total"/> is a count of.
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
    /// Can be null for a query that never had applied coniditons, one if the the conditions were completely pruned
    /// away, see <see cref="InquirySettings.IgnoreUnboundFields"/>. Either represents an unfiltered query.
    /// </remarks>
    /// <returns>null where there is nothing left to filter by</returns>
    private ICondition? Combined()
    {
        ICondition? combined = Conditions.Count switch
        {
            0 => null,
            1 => Conditions.First(),
            _ => new ConjunctionCondition(Operator.And, Conditions), // When more than one root condition was applied, they are ANDed
        };

        if ((combined is null) || (!Settings.IgnoreUnboundFields)) { return combined; }

        return ConditionPruner.Prune(combined, Bindings, Collections, field => Drop(field, BindingUse.Test));
    }

    /// <summary>
    /// The query with every sort applied, in the order given.
    /// </summary>
    /// <param name="query"></param>
    /// <returns></returns>
    /// <remarks>
    /// A sort that cannot be honoured because there is nothing to order by, a constant, or a type with no
    /// comparison of its own, is dropped rather than refused and recorded in <see cref="DroppedFields"/>.
    /// A sort on a field bound without <see cref="BindingUse.Sort"/> is a refusal and throws.
    /// </remarks>
    /// <exception cref="WeequeryException">
    /// a sort requests an unbound field, or one not bound for sorting
    /// </exception>
    private IQueryable<T> Sorted(IQueryable<T> query)
    {
        // If the sort uses an unbound field and dropping is configured, do so, see InquirySettings.IgnoreUnboundFields
        var sorts = Settings.IgnoreUnboundFields ? Sorts.Where(sort => Keep(sort.Field, BindingUse.Sort)) : Sorts;

        // If the query has already been ordered, we must use ThenBy instead of OrderBy
        bool alreadySorted = false;
        foreach (var sort in sorts)
        {
            var binding = BindingLookup.Resolve(Bindings, sort.Field);

            // If ordering was requested on a constant field, just ignore
            if (binding.IsConstant)
            {
                Drop(sort.Field, BindingUse.Sort, "it is a constant value, and would not affect ordering");

                continue;
            }

            // If the field has been bound, but not for ordering
            if (!binding.Allows(BindingUse.Sort))
            {
                throw new WeequeryException(WeequeryError.OperatorUnsupported, $"Cannot sort on '{sort.Field}': it is bound for {binding.Use}");
            }

            // If the binding doesn not represent an orderable type, just ignore
            if (!binding.IsOrderable)
            {
                Drop(sort.Field, BindingUse.Sort, $"{binding.PropertyType.Name} has no ordering");

                continue;
            }

            // Sort on the accessor type, not the unwrapped one, otherwise a Nullable<> property cannot
            // satisfy the Func<T, TKey> the sort methods want. Nullable<> keys sort fine, nulls first.
            Type keyType = binding.PropertyType;
            Expression key = binding.Accessor;

            if (binding.RequiresLinkCheck)
            {
                // The path steps through something that may not be there, so a null guard is required. A row with a missing link is a null, so a
                // value typed key must be treated as nullable to hold one. Those rows sort first, as nulls do.
                keyType = ((keyType.IsValueType) && (!binding.PropertyIsWrappedByNullable)) ? typeof(Nullable<>).MakeGenericType(keyType) : keyType;

                Expression found = (keyType == binding.PropertyType) ? binding.Accessor : Expression.Convert(binding.Accessor, keyType);

                key = Expression.Condition(binding.LinkNotNullCheck, found, Expression.Constant(null, keyType));
            }

            var clause = SortMethods.For(sort.Direction, alreadySorted, typeof(T), keyType);

            // turn the binding accessor into something usable for the call
            var selector = Expression.Lambda(clause.SelectorType, key, SharedBindingParameter);

            // Add the call to the query's own expression and let the provider make a query of it
            query = query.Provider.CreateQuery<T>(Expression.Call(null, clause.Method, query.Expression, Expression.Quote(selector)));

            alreadySorted = true;
        }

        return query;
    }

    /// <summary>
    /// How many rows a page can hold, which is the size the caller named or, where it named none,
    /// <see cref="InquirySettings.DefaultPageSize"/>.
    /// </summary>
    /// <returns>zero where nothing named a size and no default was set, which is the query that has no window</returns>
    private int EffectivePageSize()
    {
        return (PageSize > 0) ? PageSize : (Settings.DefaultPageSize ?? 0);
    }

    /// <summary>
    /// The query narrowed to the requested page, or unchanged if no paging was requested
    /// </summary>
    /// <param name="query"></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">
    /// the resolved size and the page combine past <see cref="int.MaxValue"/> rows to skip.
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
    /// Determine if this query is valid, and if not, what is wrong with it <see cref="ValidationResult"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything <see cref="Build"/> would refuse, reported rather than raised, and all of it rather than only the
    /// first encountered
    /// <code>
    /// var problems = inquiry.Validate();
    /// if (!problems.IsValid) { return BadRequest(problems.Problems.Select(problem =&gt; problem.ToString())); }
    /// </code>
    /// </para>
    /// <para>
    /// <b>Nothing is executed and nothing is kept.</b> The queries this builds to see whether they can be built
    /// are thrown away, so this costs what a build costs and changes nothing about what a later build does. The
    /// one thing it does leave behind is <see cref="DroppedFields"/>, which it fills exactly as a build fills it,
    /// since what a query quietly drops is worth knowing at the same time as what it refuses outright, see
    /// <see cref="InquirySettings.IgnoreUnboundFields"/>.
    /// </para>
    /// <para>
    /// <b>Valid means it will build</b>, which doesn't necessarily mean it will work. A provider may still refuse
    /// what it is handed <see cref="Operator.IsMatch"/> against SQL Server is the standing example. Where what a
    /// backend cannot do is known, say so on <see cref="InquirySettings.Operators"/> and that much of it is
    /// refused here instead of there.
    /// </para>
    /// </remarks>
    /// <returns>any problems found, it order of discovery; never null</returns>
    public ValidationResult Validate()
    {
        Dropped.Clear(); // should only represent the last Build() or Validate(), not cumulative

        List<ValidationProblem> problems = [];

        try
        {
            var condition = Combined();
            if (condition is not null) { Predicate(condition); }
        }
        catch (WeequeryException error) { problems.Add(new ValidationProblem(BindingUse.Test, error.Error, error.Message)); }

        // Against the unfiltered query, so a condition that refused does not take the sorts down with it
        try { Sorted(Query); }
        catch (WeequeryException error) { problems.Add(new ValidationProblem(BindingUse.Sort, error.Error, error.Message)); }

        // Only if a projection was requested
        if (!Projected.IsEmpty)
        {
            try { Projector(); }
            catch (WeequeryException error) { problems.Add(new ValidationProblem(BindingUse.Projection, error.Error, error.Message)); }
        }

        return (problems.Count == 0) ? ValidationResult.Valid : new ValidationResult(problems);
    }

    /// <summary>
    /// Determine if anything is wrong with a request prior to application. See <see cref="QueryRequest"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Should be used on externally sourced rquests prior to <see cref="ApplyRequest"/>, which will only
    /// return the first issue found.
    /// <code>
    /// var problems = inquiry.Validate(request, DefaultSort);
    /// if (!problems.IsValid) { return BadRequest(problems.Problems.Select(problem =&gt; problem.ToString())); }
    ///
    /// var (page, total) = inquiry.ApplyRequest(request, DefaultSort).BuildPagedProjected();
    /// </code>
    /// </para>
    /// <para>
    /// <b>Will not modify the Inquiry</b> A copy of the current Inquiry is created to test against, then discarded.
    /// </para>
    /// <para>
    /// <b>Two passes, each portion can report twice.</b> Reading the text and resolving what it says against the
    /// bindings are separate failures: a sort clause that will not parse is one problem, and a sort clause that
    /// parses and names an unbound field is another. Every parse failure is reported first, then everything
    /// <see cref="Validate()"/> finds in what did parse.
    /// </para>
    /// <para>
    /// <b>It validates the request with current Inquiry settings</b> a condition this Inquiry already carries 
    /// is ANDed with the request's, and is validated alongside it.
    /// </para>
    /// </remarks>
    /// <param name="request">the caller's query; null asks about this Inquiry as it stands, see <see cref="Validate()"/></param>
    /// <param name="defaultSort">what to sort by where the request named no sorts, as <see cref="ApplyRequest"/> takes it</param>
    /// <returns>the problems, parse faults first; never null</returns>
    public ValidationResult Validate(QueryRequest? request, IEnumerable<Sort>? defaultSort = null)
    {
        if (request is null) { return Validate(); }

        List<ValidationProblem> problems = [];
        var candidate = Copy();

        ParsedQuery? unpacked = null;

        try
        {
            unpacked = request.Unpack(defaultSort);
        }
        catch (WeequeryException error) { problems.Add(new ValidationProblem(BindingUse.None, error.Error, error.Message)); }

        // Not one of segments, so it is reported against the request itself
        try { candidate = candidate.ApplyPagination(request.PageSize, request.Page ?? 0); }
        catch (WeequeryException error) { problems.Add(new ValidationProblem(BindingUse.None, error.Error, error.Message)); }

        // Attempt to apply what made it through the unpack. If it didn't unpack, there is nothing to apply
        candidate = candidate
            .ApplyCondition(unpacked?.Condition)
            .ApplySorts(unpacked?.Sorts)
            .ApplyProjection(unpacked?.Projection);

        problems.AddRange(candidate.Validate().Problems);

        return (problems.Count == 0) ? ValidationResult.Valid : new ValidationResult(problems);
    }

    /// <summary>
    /// Apply all conditions, sorts, paging, etc to the wrapped IQueryable and return it
    /// </summary>
    /// <returns></returns>
    public IQueryable<T> Build()
    {
        Dropped.Clear(); // should only represent the last Build() or Validate(), not cumulative

        return Windowed(Sorted(Filtered()));
    }

    /// <summary>
    /// Apply everything as <see cref="Build"/> does, and hand back that query together with the one that counts
    /// what the page is a page of.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For the caller that has to answer "showing 21 to 40 of 387". The 387 is not something a page can be asked
    /// for. It is the size of the filtered set the window was taken from, so it is a second query over the same
    /// conditions, and this builds it alongside the first.
    /// <code>
    /// var (page, total) = query.WithWeequery()
    ///     .BindProperties(MinionBindings)
    ///     .ApplyCondition(request.Filter)
    ///     .ApplySorts(request.Sort, DefaultSort)
    ///     .ApplyPagination(request.PageSize, request.Page)
    ///     .BuildPaged();
    ///
    /// var matched = await total.FirstOrDefaultAsync();
    /// var rows    = await page.ToListAsync();
    /// </code>
    /// </para>
    /// <para>
    /// <b>The second query asks for a number, not for rows.</b> It carries the conditions and nothing else and
    /// reads back a count, so no column of the entity is in the statement and nothing of a row crosses the
    /// wire. <see cref="PagedQuery{T}.Total"/> says how to end it, and which ending answers about the count
    /// query rather than about the count.
    /// </para>
    /// <para>
    /// <b>Neither query has run.</b> Executing is left to the caller rather than done here, for two reasons. It
    /// is a database round trip, and the method that reads a number without blocking a thread is
    /// <c>FirstOrDefaultAsync</c>, which belongs to Entity Framework Core and not to this library. Weequery
    /// takes no dependency on whatever is going to execute the query, and reading it for you would mean either
    /// taking one or blocking in code that ought to be awaiting. It also stays true to what
    /// <see cref="Build"/> promises, which is a query and no execution, so both halves compose with whatever
    /// was planned.
    /// </para>
    /// <para>
    /// Total row count should come from <see cref="PagedQuery{T}.Total"/> not from the length of
    /// <see cref="PagedQuery{T}.Page"/>
    /// </para>
    /// <para>
    /// If no pagnation was applied <see cref="ApplyPagination"/> the page is the whole filtered result, and 
    /// the count agrees with its length. 
    /// </para>
    /// </remarks>
    /// <returns>the page, and the query counting everything the conditions matched; never null, neither half null</returns>
    /// <exception cref="WeequeryException">
    /// whatever <see cref="Build"/> would throw, and at the same point: the conditions and sorts are resolved
    /// against the bindings here, not when either query is enumerated
    /// </exception>
    public PagedQuery<T> BuildPaged()
    {
        Dropped.Clear(); // should only represent the last Build() or Validate(), not cumulative

        var matches = Filtered(); // share the unwindowed portion of the query

        return new PagedQuery<T>(Windowed(Sorted(matches)), Counted(matches));
    }

    /// <summary>
    /// The filtered query as a count of itself: one row holding how many matched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A grouping on a constant rather than a call to Count, because Count is terminal. It executes, and what
    /// <see cref="BuildPaged"/> promises is a query that has not. Every row of a grouping keyed on the same
    /// value lands in one group, so the size of that group is the size of the set, and it is still an
    /// IQueryable when the caller gets it.
    /// </para>
    /// <para>
    /// Providers reduce it to a single COUNT over the filtered rows, so the entity's own columns are nowhere in
    /// the statement and nothing of a row is read. See <see cref="PagedQuery{T}.Total"/> for how to end it, and
    /// for the one ending that gives a wrong answer quietly.
    /// </para>
    /// <para>
    /// A set nothing matched is no group rather than a group of none, so the query comes back empty rather than
    /// with a zero in it. That is why the ending to reach for is FirstOrDefault, whose default is the zero that
    /// was wanted, rather than Single.
    /// </para>
    /// </remarks>
    /// <param name="matches">the filtered query, with no sort and no window on it</param>
    /// <returns>a query of one number</returns>
    private static IQueryable<int> Counted(IQueryable<T> matches)
    {
        return matches.GroupBy(_ => 1).Select(rows => rows.Count());
    }

    /// <summary>
    /// Build into a projection <see cref="Build"/>, and read back only the projected fields rather than whole
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
    /// whole row thrown away afterwards: three columns of a wide table, over a page of
    /// twenty, is a different amount of work from twenty whole rows. 
    /// </para>
    /// <para>
    /// <b>Keys come back as the binding spelled them</b>, not as the caller typed them. Fields are matched
    /// without regard to case, so "name" and "NAME" both reach a binding for "Name", and all of them read
    /// back as "Name". Two callers asking differently get the same shape, as expected. Entries are added in 
    /// the order asked.
    /// </para>
    /// <para>
    /// <b>Projected values are untyped and nullable</b>, so a row is <c>object?</c> whatever the property held, 
    /// and null where the value is null or the path to it runs through a null. A field taken at an index nothing 
    /// sits at is null too, which is the same rule everywhere else, see <see cref="Operator"/>.
    /// </para>
    /// <para>
    /// If not projection is applied, this will return every bound field, including bound constants.
    /// </para>
    /// </remarks>
    /// <returns>the same query <see cref="Build"/> would return, as dictionaries rather than entities</returns>
    /// <exception cref="WeequeryException">
    /// whatever <see cref="Build"/> would throw, plus a projected field that no binding claimed or that names a
    /// bound collection
    /// </exception>
    public IQueryable<Dictionary<string, object?>> BuildProjected()
    {
        // Build will clear and (maybe) write to Dropped, Projector can append after
        return Build().Select(Projector());
    }

    /// <summary>
    /// The selector for this Inquiry's projection, see <see cref="ProjectionBuilder{T}"/>.
    /// </summary>
    /// <remarks>
    /// The builder is handed the drop test only if the caller asked for one, so it does not have to about
    /// <see cref="InquirySettings.IgnoreUnboundFields"/>, only if a field survives.
    /// </remarks>
    private Expression<Func<T, Dictionary<string, object?>>> Projector()
    {
        return ProjectionBuilder<T>.Build(Bindings, Collections, Projected, SharedBindingParameter,
            Settings.IgnoreUnboundFields ? (field => Keep(field, BindingUse.Projection)) : null);
    }

    /// <summary>
    /// Apply everything as <see cref="BuildPaged"/> does, and read back only the projected fields.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two halves a grid needs, narrowed to the columns it draws.
    /// <code>
    /// var (page, total) = query.WithWeequery()
    ///     .BindProperties(MinionBindings)
    ///     .ApplyCondition(request.Filter)
    ///     .ApplySorts(request.Sort, DefaultSort)
    ///     .ApplyPagination(request.PageSize, request.Page)
    ///     .ApplyProjection(request.Fields)
    ///     .BuildPagedProjected();
    ///
    /// var matched = await total.FirstOrDefaultAsync();
    /// var rows    = await page.ToListAsync();
    /// </code>
    /// </para>
    /// <para>
    /// <b>Only the page is projected.</b> <see cref="PagedQuery{T}.Total"/> will return the same row 
    /// count as an unprojected query.
    /// </para>
    /// </remarks>
    /// <returns>the projected page, and the query counting everything the conditions matched</returns>
    /// <exception cref="WeequeryException">whatever <see cref="BuildProjected"/> would throw</exception>
    public PagedQuery<Dictionary<string, object?>> BuildPagedProjected()
    {
        Dropped.Clear(); // should only represent the last Build() or Validate(), not cumulative

        // Share the common portion of the query
        var matches = Filtered();

        return new PagedQuery<Dictionary<string, object?>>(
            Windowed(Sorted(matches)).Select(Projector()),
            Counted(matches));
    }

    /// <summary>
    /// Build the predicate for a condition without needing an IQueryable, for use with Where, Any and friends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every predicate built for one entity type is built over the same parameter, which lets the bindings
    /// be resolved once and reused. Independent predicates do not care, but a predicate from here nested inside
    /// another over the same type (a predicate over Minion used inside "minion =&gt; minion.Peers.Any(...)", say)
    /// would have the inner parameter shadow the outer, so the inner test would read the inner element. Build the
    /// outer lambda by hand around this one, rather than combining two of these.
    /// </para>
    /// </remarks>
    /// <param name="bindingRequests"></param>
    /// <param name="condition"></param>
    /// <returns></returns>
    [SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Per entity type is the point: the bound is one per T and the builders close over T's bindings, so Inquiry<T> is where a caller already is when it needs them.")]
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
    /// values are written in as constants rather than read out of the boxes that exist to become query 
    /// parameters, see <see cref="ValueInliner"/>. The predicate selects exactly what the uncompiled 
    /// expression selects; it is cheaper to compile and to run.
    /// </para>
    /// </remarks>
    /// <param name="bindingRequests"></param>
    /// <param name="condition"></param>
    /// <param name="settings">[OPT] the rules to compile in, see <see cref="InquirySettings"/>; the defaults where none are given</param>
    /// <returns></returns>
    [SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Per entity type is the point: the bound is one per T and the builders close over T's bindings, so Inquiry<T> is where a caller already is when it needs them.")]
    public static Func<T, bool> BuildDelegate(IEnumerable<BindingRequest> bindingRequests, ICondition condition, InquirySettings? settings = null)
    {
        // This will not be translated to EF, so an IsMatch in it is bounded by MatchTimeout, the string
        // comparisons are told how to compare, and the values need not stay reachable as parameters. 
        return ValueInliner.Apply(StringComparisonRules.Apply(RegexTimeout.Apply(BuildExpression(bindingRequests, condition)), settings ?? InquirySettings.Default)).Compile();
    }
}
