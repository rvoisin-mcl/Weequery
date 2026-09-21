using Weequery.Interfaces;

namespace Weequery;

/// <summary>
/// A whole query as it arrives from a caller: what to ask, and which page of the answer to take. What
/// <see cref="Inquiry{T}.ApplyRequest"/> applies in one call.
/// </summary>
/// <remarks>
/// <para>
/// <code>
/// [HttpGet]
/// public async Task&lt;IActionResult&gt; Search([FromQuery] QueryRequest request)
/// {
///     var paged = _context.Minions
///         .WithWeequery()
///         .BindProperties(MinionBindings)
///         .ApplyRequest(request, DefaultSort)
///         .BuildPagedProjected();
///
///     return Ok(new { total = await paged.Total.FirstOrDefaultAsync(), rows = await page.ToListAsync() });
/// }
/// </code>
/// which reads as <c>?query=Pay%20&gt;%2010000%20OrderBy%20Pay%20DESC%20Select%20Name,Pay&amp;page=0&amp;pageSize=20</c>.
/// </para>
/// <para>
/// Query string only accepts the <see cref="QueryStyle.Native"/> style
/// </para>
/// <para>
/// <b>Nothing is parsed until it is asked for.</b> This is a payload rather than a query: the members are the
/// text as it arrived, and reading them costs nothing and refuses nothing. Malformed text becomes a
/// <see cref="WeequeryException"/> when it is unpacked, which is when a model binder has already finished and a
/// request handler is somewhere it can answer with a bad request. Or ask
/// <see cref="Inquiry{T}.Validate(QueryRequest, IEnumerable{Sort}?)"/> first and get the whole list
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
    /// The query, filtering, sorting, projection: <c>"Pay &gt; 10000 OrderBy Pay DESC Select Name, Pay"</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every part of it is optional, see <see cref="ParsedQuery"/>
    /// </para>
    /// </remarks>
    public string? Query { get; set; }

    /// <summary>
    /// Which page to take, counting from zero.
    /// </summary>
    /// <remarks>
    /// Null or a invalid # will evaluate as the first page <see cref="Inquiry{T}.ApplyPagination"/>.
    /// </remarks>
    public int? Page { get; set; }

    /// <summary>
    /// How many rows the page holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null or an invalid # evaluates as <see cref="InquirySettings.DefaultPageSize"/> if set. See
    /// <see cref="Inquiry{T}.ApplyPagination"/>.
    /// </para>
    /// </remarks>
    public int? PageSize { get; set; }

    /// <summary>
    /// Everything this request asks for.
    /// </summary>
    /// <remarks>
    /// The way to read one, and what <see cref="Inquiry{T}.ApplyRequest"/> uses. The three methods below answer
    /// about one part each, for the caller who wants only one; each reads the whole of
    /// <see cref="Query"/> to do it, so asking for all three that way reads it three times and this reads it
    /// once.
    /// </remarks>
    /// <param name="defaultSort">what to sort by where the request named nothing to sort by</param>
    /// <returns>never null, though every part of it may be empty</returns>
    /// <exception cref="WeequeryException">
    /// <see cref="Query"/> is malformed, or spells something a way <see cref="QueryStyle.Native"/> refuses
    /// </exception>
    public ParsedQuery Unpack(IEnumerable<Sort>? defaultSort = null)
    {
        return ParsedQuery.Parse(Query, defaultSort, QueryStyle.Native);
    }

    /// <summary>
    /// The condition this request carries.
    /// </summary>
    /// <returns>null where the request named no condition at all, which is a filter on nothing</returns>
    /// <exception cref="WeequeryException"><inheritdoc cref="Unpack" path="/exception"/></exception>
    public ICondition? UnpackCondition()
    {
        return Unpack().Condition;
    }

    /// <summary>
    /// The sorts this request asked for, or the default where it asked for none.
    /// </summary>
    /// <param name="defaultSort">
    /// what to sort by when the caller named nothing, which is worth supplying wherever the query is paged: a
    /// page of an unordered query holds arbitrary rows
    /// </param>
    /// <returns>never null; empty where nothing was named and no default was given</returns>
    /// <exception cref="WeequeryException"><inheritdoc cref="Unpack" path="/exception"/></exception>
    public List<Sort> UnpackSorts(IEnumerable<Sort>? defaultSort = null)
    {
        return Unpack(defaultSort).Sorts;
    }

    /// <summary>
    /// The fields this request asked to read back.
    /// </summary>
    /// <returns><see cref="Weequery.Projection.None"/> where it named none; never null</returns>
    /// <exception cref="WeequeryException"><inheritdoc cref="Unpack" path="/exception"/></exception>
    public Projection UnpackProjection()
    {
        return Unpack().Projection;
    }
}
