using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Text;
using System.Text.RegularExpressions;
using Weequery.Builders;
using Weequery.Interfaces;

namespace Weequery;

/// <summary>
/// Wrapper class that provides fluent configuration for IQueryable with Weequery
/// </summary>
/// <remarks>
/// The bound properties are the allow-list: a condition or a sort naming a field that no binding claimed is
/// refused. Field names are matched against binding keys without regard to case.
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
    /// <see cref="BindingSets"/>, and costs nothing to do — an expression tree is immutable, and a parameter is an
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
    private List<ICondition> Conditions { get; init; } = new();
    private List<Sort> Sorts { get; init; } = new();
    private int PageSize { get; set; } = -1;
    private int Page { get; set; } = -1;

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
    /// Bind the property indicated by the selector func, if a key is not provided, it will be bound as the property path
    /// </summary>
    /// <typeparam name="TProperty"></typeparam>
    /// <param name="selector"></param>
    /// <param name="key"></param>
    /// <returns></returns>
    public Inquiry<T> BindProperty<TProperty>(Expression<Func<T, TProperty>> selector, string? key = null)
    {
        Binding<T>.Create(SharedBindingParameter, selector, Bindings, key);

        return this;
    }

    /// <summary>
    /// Bind the property reached by following the selector and then the segments after it, for a path a selector
    /// cannot write on its own. If a key is not provided, the last segment is used.
    /// </summary>
    /// <remarks>
    /// The case this exists for is a path through a <see cref="Nullable{T}"/>: C# will not compile
    /// <c>(x) =&gt; x.BirthDate.Year</c> against a <c>DateTime?</c>, since a Nullable exposes only its own members,
    /// and writing <c>(x) =&gt; x.BirthDate!.Value.Year</c> instead unwraps rather than reaches through, binding a
    /// plain int with no null of its own. Selecting BirthDate and naming "Year" as a segment binds
    /// "BirthDate.Year" exactly as <see cref="BindProperty(string, string?)"/> would, while the compiler still
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
    /// <exception cref="WeequeryException"></exception>
    public Inquiry<T> BindProperty<TProperty>(Expression<Func<T, TProperty>> selector, string[] segments, string? key = null)
    {
        Binding<T>.Create(SharedBindingParameter, selector, segments, Bindings, key);

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
    /// <exception cref="WeequeryException"></exception>
    public Inquiry<T> BindConstant<TValue>(string key, TValue value)
    {
        WeequeryException.ThrowIfNullOrEmpty(key);
        WeequeryException.ThrowIfNotBindingKey(key);

        Binding<T>.CreateConstant(SharedBindingParameter, key, value, Bindings);

        return this;
    }

    /// <summary>
    /// Bind the property with the provided path, if a key is not provided, it will be bound as the property path
    /// </summary>
    /// <param name="path"></param>
    /// <param name="key"></param>
    /// <returns></returns>
    public Inquiry<T> BindProperty(string path, string? key = null)
    {
        WeequeryException.ThrowIfNullOrEmpty(path);
        WeequeryException.ThrowIfNotNullButEmpty(key);
        WeequeryException.ThrowIfNotBindingKey(key);

        Binding<T>.Create(SharedBindingParameter, path, Bindings, key);

        return this;
    }

    /// <summary>
    /// Bind the properties in the list, if a key is not provided, they will be bound as the property path
    /// </summary>
    /// <remarks>
    /// A set of requests is resolved once for the process and kept, see <see cref="BindingSets"/>, so calling this
    /// per request costs a copy rather than a property path lookup per property. Adding to this Inquiry after it,
    /// with this or with <see cref="BindProperty(string, string?)"/>, works as it always did: everything binds
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
        foreach (var binding in BindingsFor(bindingRequests))
        {
            if (Bindings.ContainsKey(binding.Key)) { throw new WeequeryException($"Binding already exists for '{binding.Key}'"); }

            Bindings[binding.Key] = binding.Value;
        }

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
    /// <param name="query">eg. "(Pay &gt; 10000) &amp;&amp; !(Name StartsWith 'Temp')"</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">the query is malformed, see <see cref="ConditionFunctions.ParseQuery"/></exception>
    public Inquiry<T> ApplyCondition(string query)
    {
        var condition = ConditionFunctions.ParseQuery(query);
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
            if (condition is null) { throw new WeequeryException($"{nameof(conditions)}[{index}] is null"); }

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
    /// <returns></returns>
    /// <exception cref="WeequeryException">the clause is malformed, see <see cref="Sort.Parse"/></exception>
    public Inquiry<T> ApplySorts(string? sortString, IEnumerable<Sort>? defaultSort = null)
    {
        return ApplySorts(Sort.Parse(sortString, defaultSort));
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
            if (sort is null) { throw new WeequeryException($"{nameof(sorts)}[{index}] is null"); }

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
        if (pageSize <= 0) { throw new WeequeryException($"{nameof(pageSize)} must be > 0"); }
        if (page < 0) { throw new WeequeryException($"{nameof(page)} must be >= 0"); }

        long skip = (long)pageSize * page;
        if (skip > int.MaxValue)
        {
            throw new WeequeryException($"{nameof(pageSize)} {pageSize} * {nameof(page)} {page} exceeds {int.MaxValue}");
        }

        PageSize = pageSize;
        Page = page;

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
        var predicate = ExpressionBuilder.BuildExpression(Bindings, condition);

        // LINQ to Objects, which is what AsQueryable over a list gives. Anything else is a provider that will be
        // handed the expression rather than running it here.
        return (Query.Provider is EnumerableQuery) ? RegexTimeout.Apply(predicate) : predicate;
    }

    /// <summary>
    /// Apply all conditions, sorts, paging, etc to the wrapped IQueryable and return it
    /// </summary>
    /// <returns></returns>
    public IQueryable<T> Build()
    {
        IQueryable<T> query = Query;

        switch (Conditions.Count)
        {
            case 0:
                break;

            case 1:
                query = query.Where(Predicate(Conditions.First()));
                break;

            default:
                // If >1 root condition was provided, wrap all root conditions inside an AND condition
                query = query.Where(Predicate(new ConjunctionCondition(Operator.And, Conditions)));
                break;
        }

        // Once the query has been sorted once, subsequent sorts must chain with ThenBy rather than restart with OrderBy
        bool alreadySorted = false;
        foreach (var sort in Sorts)
        {
            if (!Bindings.TryGetValue(sort.Field, out var binding)) { throw new WeequeryException($"Unbound field: '{sort.Field}'"); }

            // The same for every row, so there is nothing here to put in order
            if (binding.IsConstant)
            {
                throw new WeequeryException($"Cannot sort on '{sort.Field}', it is a constant");
            }

            // Refused here rather than left to the comparer
            if (!binding.IsOrderable)
            {
                throw new WeequeryException($"Cannot sort on '{sort.Field}', {binding.PropertyType.Name} has no ordering");
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

        if (PageSize > 0)
        {
            query = query.Skip(PageSize * Page).Take(PageSize);
        }

        return query;
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

        return ExpressionBuilder.BuildExpression(BindingsFor(bindingRequests), condition);
    }

    /// <summary>
    /// The binding sets built for this entity type, keyed by the requests that produced them.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Dictionary<string, Binding<T>>> BindingSets = new();

    /// <summary>
    /// How many distinct binding sets to hold. Sets come from code, so an application has a handful and this is
    /// never reached; the cap is only here so that a caller composing sets dynamically cannot grow the cache
    /// without bound. Past it, bindings are built per call.
    /// </summary>
    private const int MaxCachedBindingSets = 64;

    /// <summary>
    /// The bindings for a set of requests, built once and kept, see <see cref="BindingSets"/>.
    /// </summary>
    /// <param name="bindingRequests"></param>
    /// <returns>a lookup that must be treated as read only, since it is shared</returns>
    /// <exception cref="WeequeryException">a request names a property that cannot be bound, or two claim one key</exception>
    private static Dictionary<string, Binding<T>> BindingsFor(IEnumerable<BindingRequest> bindingRequests)
    {
        // Read once: the requests may be a lazy sequence, and the key has to describe the same set that gets built
        var requests = (bindingRequests as IReadOnlyList<BindingRequest>) ?? bindingRequests.ToList();

        var key = CacheKey(requests);
        if (BindingSets.TryGetValue(key, out var cached)) { return cached; }

        // Against the shared parameter, so these compose with anything else bound for this type
        Dictionary<string, Binding<T>> bindings = BindingLookup.Create<T>();
        foreach (var bindingDefinition in requests)
        {
            Binding<T>.Create(SharedBindingParameter, bindingDefinition.PropertyPath, bindings, bindingDefinition.Key);
        }

        // Two threads meeting on the same new set both build one, and either will do
        if (BindingSets.Count < MaxCachedBindingSets) { BindingSets.TryAdd(key, bindings); }

        return bindings;
    }

    /// <summary>
    /// Describes a set of requests exactly, so two sets share an entry only when they would build the same
    /// bindings. The separators cannot appear in a path or a key, both of which are SQL names, dotted for a path.
    /// </summary>
    /// <param name="requests"></param>
    /// <returns></returns>
    private static string CacheKey(IReadOnlyList<BindingRequest> requests)
    {
        StringBuilder builder = new();

        foreach (var request in requests)
        {
            builder.Append(request.PropertyPath).Append('>').Append(request.Key).Append('|');
        }

        return builder.ToString();
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
    /// </remarks>
    /// <param name="bindingRequests"></param>
    /// <param name="condition"></param>
    /// <returns></returns>
    public static Func<T, bool> BuildDelegate(IEnumerable<BindingRequest> bindingRequests, ICondition condition)
    {
        // Nothing is going to translate this one, so an IsMatch in it is bounded by MatchTimeout
        return RegexTimeout.Apply(BuildExpression(bindingRequests, condition)).Compile();
    }
}