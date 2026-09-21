namespace Weequery;

/// <summary>
/// A page of a query, and the query that counts what it is a page of. What <see cref="Inquiry{T}.BuildPaged"/>
/// hands back.
/// </summary>
/// <remarks>
/// <para>
/// Both are queries, and neither has run. A grid wants two answers — the rows to draw, and how many rows there
/// are altogether so it knows how many pages to offer — and those are two statements against the database
/// whichever way they are arrived at. Weequery builds both and leaves the running of them to you, which is what
/// lets the count be awaited rather than blocked on, and what keeps this library free of a dependency on
/// whatever is going to execute it.
/// </para>
/// <code>
/// var (page, matches) = context.Minions
///     .WithWeequery()
///     .BindProperties(MinionBindings)
///     .ApplyCondition(request.Filter)
///     .ApplySorts(request.Sort, DefaultSort)
///     .ApplyPagination(request.PageSize, request.Page)
///     .BuildPaged();
///
/// var total = await matches.CountAsync();
/// var rows  = await page.ToListAsync();
/// </code>
/// <para>
/// Deconstructs, as above, so the two come apart where they are used.
/// </para>
/// </remarks>
/// <typeparam name="T"></typeparam>
/// <param name="Page">
/// the rows the caller asked for: every condition, then every sort, then the window. This is exactly what
/// <see cref="Inquiry{T}.Build"/> returns, and is the same query.
/// </param>
/// <param name="Matches">
/// every row the conditions matched, with no sort and no window. Count this.
/// <para>
/// Not sorted, because there is no ordering a count depends on and asking a database to produce one it will then
/// discard is work for nothing. Not paged, because the point of it is the number the window was taken from.
/// </para>
/// <para>
/// It is a query rather than a number, so it may be counted, or enumerated, or composed onto, or ignored
/// entirely where the caller did not ask for a total. Enumerating it returns everything that matched, so count it
/// unless that is genuinely what you meant.
/// </para>
/// </param>
public record PagedQuery<T>(IQueryable<T> Page, IQueryable<T> Matches) where T : class;
