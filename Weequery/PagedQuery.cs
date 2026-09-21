namespace Weequery;

/// <summary>
/// A page of a query, and the query that counts what it is a page of. What <see cref="Inquiry{T}.BuildPaged"/>
/// hands back.
/// </summary>
/// <remarks>
/// <para>
/// Both are queries, and neither has run. A grid wants two answers, the rows to draw and how many rows there
/// are altogether so it knows how many pages to offer, and those are two statements against the database
/// whichever way they are arrived at. Weequery builds both and leaves the running of them to you, which is what
/// lets the count be awaited rather than blocked on, and what keeps this library free of a dependency on
/// whatever is going to execute it.
/// </para>
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
/// <para>
/// <b>The total is a query of one number, so how it is read matters.</b> It is one row holding a count rather
/// than a row per match, and the endings that read a sequence answer about the sequence instead of about the
/// number in it:
/// </para>
/// <list type="table">
/// <item>
///   <term>FirstOrDefaultAsync()</term>
///   <description>the total, and zero where nothing matched, which is the right answer</description>
/// </item>
/// <item>
///   <term>SingleAsync()</term>
///   <description>
///     the total, but it throws where nothing matched: a grouping over no rows is no group, so the query comes
///     back with no row at all
///   </description>
/// </item>
/// <item>
///   <term>CountAsync()</term>
///   <description>
///     1, or 0 where nothing matched. It counts the rows of the count. This is the one to watch for, because it
///     compiles, it looks like the obvious thing to write, and the number it gives back is wrong quietly
///   </description>
/// </item>
/// </list>
/// <para>
/// <b>Why it is shaped as a grouping.</b> LINQ has no scalar query: Count is a terminal operation, so there is
/// no IQueryable that is a count and nothing to hold back and hand to a caller. A grouping on a constant is the
/// only count that stays a query, and every provider tested reduces it to one COUNT over the filtered set:
/// </para>
/// <code>
/// SELECT COUNT(*)
/// FROM (
///     SELECT 1 AS "Key"
///     FROM "Minions" AS "m"
///     WHERE "m"."IsActive"
/// ) AS "m0"
/// GROUP BY "m0"."Key"
/// </code>
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
/// Nothing else is right: see the remarks on <see cref="PagedQuery{T}"/> for what the other endings do.
/// </para>
/// <para>
/// It asks the database for a number rather than for rows, so nothing of a row crosses the wire and the
/// entity's own shape is nowhere in the statement. It carries no sort, because there is no ordering a count
/// depends on and asking a database to produce one it will then discard is work for nothing, and no window,
/// because the point of it is the number the window was taken from.
/// </para>
/// </param>
public record PagedQuery<T>(IQueryable<T> Page, IQueryable<int> Total);
