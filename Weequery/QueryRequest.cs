using Weequery.Interfaces;

namespace Weequery;

/// <summary>
/// A whole query as it arrives from a caller: what to filter by, what to sort by, which columns to read back and
/// which page of the result to take. What <see cref="Inquiry{T}.ApplyRequest"/> applies in one call.
/// </summary>
/// <remarks>
/// <para>
/// Every member is a scalar, so the whole of it binds off a query string. That is the shape it exists for:
/// <code>
/// [HttpGet]
/// public async Task&lt;IActionResult&gt; Search([FromQuery] QueryRequest request)
/// {
///     var (page, matches) = _context.Minions
///         .WithWeequery()
///         .BindProperties(MinionBindings)
///         .ApplyRequest(request, DefaultSort)
///         .BuildPagedProjected();
///
///     return Ok(new { total = await matches.CountAsync(), rows = await page.ToListAsync() });
/// }
/// </code>
/// which reads as <c>?filter=Pay%20&gt;%2010000&amp;sort=Pay%20DESC&amp;fields=Name,Pay&amp;page=0&amp;pageSize=20</c>.
/// </para>
/// <para>
/// <b>It grants nothing.</b> A request names fields; the bindings decide whether it may. Everything a caller
/// writes here is held to the same allow-list it would be held to arriving any other way, and a request naming a
/// field nothing bound is refused exactly as a bare condition string is, see
/// <see cref="Inquiry{T}.BindProperty(string, string?, BindingUse, ValueConverter?)"/>.
/// </para>
/// <para>
/// <b>Nothing is parsed until it is asked for.</b> This is a payload rather than a query: the members are the
/// text as it arrived, and reading them costs nothing and refuses nothing. Malformed text becomes a
/// <see cref="WeequeryException"/> when it is unpacked, which is when a model binder has already finished and a
/// request handler is somewhere it can answer with a bad request. Or ask
/// <see cref="Inquiry{T}.Validate(QueryRequest, IEnumerable{Sort}?, QueryStyle)"/> first and get the whole list
/// of what is wrong with it rather than the first thing.
/// </para>
/// <para>
/// A plain object with settable properties rather than a record, because a model binder has to be able to
/// construct it empty and fill it in a piece at a time.
/// </para>
/// </remarks>
public class QueryRequest
{
    /// <summary>
    /// The condition, in the query language: <c>"(Pay &gt; 10000) AND (IsActive = true)"</c>.
    /// </summary>
    /// <remarks>
    /// Null or empty is no filtering, which is every row the query already had. Read by
    /// <see cref="UnpackCondition"/>, and ignored where <see cref="Condition"/> carries one, since that is the
    /// more particular form of the same thing. See <see cref="ConditionFunctions.ParseQuery"/>.
    /// </remarks>
    public string? Filter { get; set; }

    /// <summary>
    /// The condition as an object graph, where it was sent that way, which is a JSON body rather than a query
    /// string.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same two forms <see cref="TransportCondition"/> carries, and the same rule for choosing between
    /// them: this one wins where both arrived. Null on anything bound off a query string, there being no way to
    /// write a tree in one, so a request that only ever travels that way never sees this member at all.
    /// </para>
    /// </remarks>
    public PackedCondition? Condition { get; set; }

    /// <summary>
    /// The sort clause, in the sort language: <c>"Pay DESC, Name"</c>.
    /// </summary>
    /// <remarks>
    /// Its own little language, separate from the condition one, so the two travel apart and neither has to know
    /// about the other. Null, empty or whitespace takes whatever default the caller of
    /// <see cref="Inquiry{T}.ApplyRequest"/> supplied. See <see cref="Weequery.Sort.Parse"/>.
    /// </remarks>
    public string? Sort { get; set; }

    /// <summary>
    /// Which fields to read back, as a comma separated list: <c>"Name, Pay"</c>.
    /// </summary>
    /// <remarks>
    /// Null or empty names nothing, and a query that projects and names nothing reads every field the caller is
    /// allowed to read, which is the allow-list's own answer to "all of it". Only read at all by the builds that
    /// project, see <see cref="Inquiry{T}.BuildProjected"/>. See <see cref="Weequery.Projection.Parse"/>.
    /// </remarks>
    public string? Fields { get; set; }

    /// <summary>
    /// Which page to take, counting from zero.
    /// </summary>
    /// <remarks>
    /// Null is the first page, so a caller that names a size and no page gets the front of the result rather
    /// than nothing. So is a negative one, there being nothing behind the first page to ask for, see
    /// <see cref="Inquiry{T}.ApplyPagination"/>.
    /// </remarks>
    public int? Page { get; set; }

    /// <summary>
    /// How many rows the page holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Anything that could not hold a page (null, zero, a negative) takes
    /// <see cref="InquirySettings.DefaultPageSize"/>, and where that was not set either there is no window and
    /// the whole result comes back. A caller that leaves the field out and a caller that sends
    /// <c>pageSize=0</c> have made the same mistake and get the same answer, see
    /// <see cref="Inquiry{T}.ApplyPagination"/>.
    /// </para>
    /// <para>
    /// <b>Nothing here caps it.</b> A caller asking for a page of a million is asking a legitimate question of
    /// this type and will get an answer; whether your database wants to be asked it is a different matter. Clamp
    /// it on the way in where that matters.
    /// </para>
    /// </remarks>
    public int? PageSize { get; set; }

    /// <summary>
    /// The condition this request carries, whichever form it arrived in.
    /// </summary>
    /// <remarks>
    /// Prefers <see cref="Condition"/> over <see cref="Filter"/> where a payload somehow carried both, the
    /// packed form being the one that needs no parsing and cannot be spelled two ways. The same rule
    /// <see cref="TransportCondition.Unpack"/> follows.
    /// </remarks>
    /// <param name="style">
    /// how strictly to read <see cref="Filter"/>. <see cref="QueryStyle.Native"/>, the default, accepts one
    /// spelling per operator. Says nothing about <see cref="Condition"/>, which carries operators as values
    /// rather than as text. See <see cref="ConditionFunctions.ParseQuery"/>
    /// </param>
    /// <returns>null where the request named no condition at all, which is a filter on nothing</returns>
    /// <exception cref="WeequeryException"><see cref="Filter"/> is malformed</exception>
    public ICondition? UnpackCondition(QueryStyle style = QueryStyle.Native)
    {
        if (Condition is not null) { return Condition.Unpack(); }

        return string.IsNullOrWhiteSpace(Filter) ? null : ConditionFunctions.ParseQuery(Filter, style);
    }

    /// <summary>
    /// The sorts this request asked for, or the default where it asked for none.
    /// </summary>
    /// <param name="defaultSort">
    /// what to sort by when the caller named nothing, which is worth supplying wherever the query is paged: a
    /// page of an unordered query holds arbitrary rows
    /// </param>
    /// <param name="style">
    /// <see cref="QueryStyle.Native"/> to take only the one word OrderBy prefix, refusing ORDER BY. Null, the
    /// default, takes both
    /// </param>
    /// <returns>never null; empty where nothing was named and no default was given</returns>
    /// <exception cref="WeequeryException"><see cref="Sort"/> is malformed</exception>
    public List<Sort> UnpackSorts(IEnumerable<Sort>? defaultSort = null, QueryStyle? style = null)
    {
        return Weequery.Sort.Parse(Sort, defaultSort, style);
    }

    /// <summary>
    /// The fields this request asked to read back.
    /// </summary>
    /// <returns><see cref="Weequery.Projection.None"/> where it named none; never null</returns>
    /// <exception cref="WeequeryException"><see cref="Fields"/> is malformed</exception>
    public Projection UnpackProjection()
    {
        return Weequery.Projection.Parse(Fields);
    }
}
