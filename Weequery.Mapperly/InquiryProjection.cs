using System.Diagnostics.CodeAnalysis;
namespace Weequery.Mapperly;

/// <summary>
/// Reads the rows of an <see cref="Inquiry{T}"/> back as your own type, through a Mapperly generated queryable
/// projection.
/// </summary>
/// <remarks>
/// <para>
/// Weequery decides <b>which rows</b>: the conditions, the sorts and the window, all against the entity and its
/// allow-list. Mapperly decides <b>what a row looks like</b>, and decided it at compile time.
/// </para>
/// <code>
/// [Mapper]
/// public partial class MinionMapper
/// {
///     [MapProperty(nameof(Minion.Pay), nameof(MinionSummary.Salary))]
///     public partial MinionSummary ToSummary(Minion minion);
///
///     public partial IQueryable&lt;MinionSummary&gt; Project(IQueryable&lt;Minion&gt; minions);
/// }
///
/// var page = context.Minions
///     .WithWeequery()
///     .BindProperties(MinionBindings)
///     .ApplyCondition(request.Filter)
///     .ApplySorts(request.Sort, DefaultSort)
///     .ApplyPagination(request.PageSize, request.Page)
///     .ProjectTo(mapper.Project);
/// </code>
/// <para>
/// <b>Neither type argument has to be written out</b>, unlike the AutoMapper and Mapster companions. The
/// projection is passed as a function, so the entity comes from the Inquiry and the DTO comes from what that
/// function returns, and C# has everything it needs.
/// </para>
/// <para>
/// <b>The order is filter, sort, window, then project</b>, which is the order that makes the window mean
/// anything: paging is over the entity's own sort, and projecting first would leave the generated output to be
/// sorted by names the DTO may not even have.
/// </para>
/// <para>
/// <b>Configure the rename on an object mapping, not on the projection.</b> Mapperly refuses
/// <c>[MapProperty]</c> on a queryable projection method, warning RMG065, and a projection left to map by name
/// alone silently leaves the unmatched member at its default. Declaring an object mapping method beside the
/// projection, as above, is what carries the configuration into it. That is Mapperly's rule rather than this
/// package's, and it is repeated here because the failure it produces is a zero rather than an error.
/// </para>
/// </remarks>
public static class InquiryProjection
{
    /// <summary>
    /// Build the query as <see cref="Inquiry{T}.Build"/> does, and hand it to the projection.
    /// </summary>
    /// <remarks>
    /// The projection has to compose onto the query rather than run it, which a Mapperly generated
    /// <c>IQueryable</c> method does: it is a <c>Select</c> over what it was given. One that enumerates instead
    /// would read every matching row before the DTO was applied, and no exception here would tell you.
    /// </remarks>
    /// <typeparam name="T">the entity</typeparam>
    /// <typeparam name="TDto">what to read back, inferred from <paramref name="project"/></typeparam>
    /// <param name="inquiry"></param>
    /// <param name="project">
    /// the generated projection, as a method group: <c>mapper.Project</c>
    /// </param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">
    /// whatever <see cref="Inquiry{T}.Build"/> would throw, the projection is null or returned null, or a
    /// projection was applied, see <see cref="RefuseAppliedProjection"/>
    /// </exception>
    [RequiresDynamicCode("Weequery closes generic types over the property types it binds, which Native AOT cannot generate at runtime")]
    [RequiresUnreferencedCode("Weequery reaches properties by the name a caller gives at runtime, so trimming cannot know which ones to keep")]
    public static IQueryable<TDto> ProjectTo<T, TDto>(this Inquiry<T> inquiry, Func<IQueryable<T>, IQueryable<TDto>> project)
        where T : class
    {
        WeequeryException.ThrowIfNull(inquiry);
        WeequeryException.ThrowIfNull(project);

        RefuseAppliedProjection(inquiry, nameof(ProjectTo));

        return Projected(project, inquiry.Build());
    }

    /// <summary>
    /// Build both halves as <see cref="Inquiry{T}.BuildPaged"/> does, and hand the page to the projection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reason this package exists. Writing it by hand is easy to get subtly wrong, because only one of the
    /// two queries should be projected:
    /// <code>
    /// var (page, total) = inquiry.ProjectToPaged(mapper.Project);
    ///
    /// var matched = await total.FirstOrDefaultAsync();
    /// var rows    = await page.ToListAsync();
    /// </code>
    /// </para>
    /// <para>
    /// <b>The count is never projected.</b> It counts rows rather than what is read off them, so it asks for
    /// a number and reads no column at all. A DTO whose mapping holds anything Mapperly cannot project cannot
    /// fail a count that never needed it, because the count never touches the mapping. See
    /// <see cref="PagedQuery{T}.Total"/> for how to read the number back.
    /// </para>
    /// <para>
    /// Neither query has run, exactly as <see cref="Inquiry{T}.BuildPaged"/> promises.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">the entity</typeparam>
    /// <typeparam name="TDto">what to read the page back as, inferred from <paramref name="project"/></typeparam>
    /// <param name="inquiry"></param>
    /// <param name="project">the generated projection, as a method group</param>
    /// <returns>the projected page, and the query counting everything the conditions matched</returns>
    /// <exception cref="WeequeryException">
    /// whatever <see cref="Inquiry{T}.BuildPaged"/> would throw, the projection is null or returned null, or a
    /// projection was applied
    /// </exception>
    [RequiresDynamicCode("Weequery closes generic types over the property types it binds, which Native AOT cannot generate at runtime")]
    [RequiresUnreferencedCode("Weequery reaches properties by the name a caller gives at runtime, so trimming cannot know which ones to keep")]
    public static PagedQuery<TDto> ProjectToPaged<T, TDto>(this Inquiry<T> inquiry, Func<IQueryable<T>, IQueryable<TDto>> project)
        where T : class
    {
        WeequeryException.ThrowIfNull(inquiry);
        WeequeryException.ThrowIfNull(project);

        RefuseAppliedProjection(inquiry, nameof(ProjectToPaged));

        var paged = inquiry.BuildPaged();

        return new PagedQuery<TDto>(Projected(project, paged.Page), paged.Total);
    }

    /// <summary>
    /// Apply the projection, and refuse a null where a query was promised.
    /// </summary>
    /// <remarks>
    /// The projection is a function this package did not write, so the one thing worth checking is that
    /// something came back. A null returned here would otherwise surface as a
    /// <see cref="NullReferenceException"/> from wherever the query was eventually enumerated, a long way from
    /// the method that produced it.
    /// </remarks>
    /// <typeparam name="T"></typeparam>
    /// <typeparam name="TDto"></typeparam>
    /// <param name="project"></param>
    /// <param name="query">what to project, already built</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">the projection returned null</exception>
    private static IQueryable<TDto> Projected<T, TDto>(Func<IQueryable<T>, IQueryable<TDto>> project, IQueryable<T> query)
        where T : class
    {
        var projected = project(query);

        if (projected is null)
        {
            throw new WeequeryException(WeequeryError.UsageInvalid, $"The projection returned null rather than a query of {typeof(TDto).Name}");
        }

        return projected;
    }

    /// <summary>
    /// Refuse an Inquiry that already had a projection applied.
    /// </summary>
    /// <remarks>
    /// Two answers to one question. <see cref="Inquiry{T}.ApplyProjection(string?)"/> says a row is the keys the
    /// caller named, and a DTO says a row is the DTO; going ahead would honour the second and drop the first
    /// without saying so, which for a caller whose projection came from a request is a filter's worth of
    /// intention quietly discarded. Say it instead.
    /// </remarks>
    /// <typeparam name="T"></typeparam>
    /// <param name="inquiry"></param>
    /// <param name="called">the method being called, so the message names what the caller wrote</param>
    /// <exception cref="WeequeryException">a projection was applied</exception>
    private static void RefuseAppliedProjection<T>(Inquiry<T> inquiry, string called)
        where T : class
    {
        if (inquiry.AppliedProjection.IsEmpty) { return; }

        throw new WeequeryException(WeequeryError.UsageInvalid, $"{called} cannot be used on an Inquiry that has already had ApplyProjection('{inquiry.AppliedProjection.ToQuery()}') called on it: the DTO decides what a row holds here, so the projected fields would be silently ignored. Drop one of the two");
    }
}
