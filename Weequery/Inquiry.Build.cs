using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Weequery.Bindings;
using Weequery.Builders;
using Weequery.Interfaces;

namespace Weequery;

// The terminal operations: the composed query, the paged pair, the projected rows, and the two static builders
// that hand out an expression or a delegate with no query behind them.
public partial class Inquiry<T> where T : class
{
    /// <summary>
    /// Apply all conditions, sorts, paging, etc to the wrapped IQueryable and return it
    /// </summary>
    /// <returns></returns>
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    [RequiresDynamicCode(AotMessages.RuntimeGenerics)]
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
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    [RequiresDynamicCode(AotMessages.RuntimeGenerics)]
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
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    [RequiresDynamicCode(AotMessages.RuntimeGenerics)]
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
    [RequiresDynamicCode(AotMessages.RuntimeGenerics)]
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
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
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    [RequiresDynamicCode(AotMessages.RuntimeGenerics)]
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
    [RequiresDynamicCode(AotMessages.RuntimeGenerics)]
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
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
    [RequiresDynamicCode(AotMessages.RuntimeGenerics)]
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    [SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Per entity type is the point: the bound is one per T and the builders close over T's bindings, so Inquiry<T> is where a caller already is when it needs them.")]
    public static Func<T, bool> BuildDelegate(IEnumerable<BindingRequest> bindingRequests, ICondition condition, InquirySettings? settings = null)
    {
        // This will not be translated to EF, so an IsMatch in it is bounded by MatchTimeout, the string
        // comparisons are told how to compare, and the values need not stay reachable as parameters. 
        return ValueInliner.Apply(StringComparisonRules.Apply(RegexTimeout.Apply(BuildExpression(bindingRequests, condition)), settings ?? InquirySettings.Default)).Compile();
    }
}
