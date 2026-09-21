namespace Weequery;

/// <summary>
/// A page of a query, and the query it is a page of. What <see cref="Inquiry{T}.BuildPaged"/>
/// hands back.
/// </summary>
/// <remarks>
/// <code>
/// var (page, total) = context.Minions
///     .WithWeequery()
///     .BindProperties(MinionBindings)
///     .ApplyCondition(request.Filter)
///     .ApplySorts(request.Sort, DefaultSort)
///     .ApplyPagination(request.PageSize, request.Page)
///     .BuildPaged();
///
/// var matched = await total.FirstOrDefaultAsync();
/// var rows    = await page.ToListAsync();
/// </code>
/// <para>
/// Deconstructs, as above, so the two come apart where they are used.
/// </para>
/// </remarks>
/// <typeparam name="T">what a row of the page reads back as</typeparam>
/// <param name="Page">
/// the rows the caller asked for: every condition, then every sort, then the window. This is exactly what
/// <see cref="Inquiry{T}.Build"/> returns, and is the same query.
/// </param>
/// <param name="Total">
/// how many rows the conditions matched, as a query of one number.
/// <para>
/// <b>Read it with <c>FirstOrDefaultAsync</c></b>, or <c>FirstOrDefault</c> where there is nothing to await.
/// </para>
/// </param>
public record PagedQuery<T>(IQueryable<T> Page, IQueryable<int> Total);
