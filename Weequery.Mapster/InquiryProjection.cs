using Mapster;
using System.Diagnostics.CodeAnalysis;

namespace Weequery.Mapster;

/// <summary>
/// Reads the rows of an <see cref="Inquiry{T}"/> back as your own type, through Mapster.
/// </summary>
/// <remarks>
/// <para>
/// Weequery decides <b>which rows</b>: the conditions, the sorts and the window, all against the entity and its
/// allow-list. Mapster decides <b>what a row looks like</b>: the DTO's own mapping, projected into the query so
/// only the columns it needs leave the database.
/// </para>
/// <code>
/// var page = context.Minions
///     .WithWeequery()
///     .BindProperties(MinionBindings)
///     .ApplyCondition(request.Filter)
///     .ApplySorts(request.Sort, DefaultSort)
///     .ApplyPagination(request.PageSize, request.Page)
///     .ProjectTo&lt;Minion, MinionSummary&gt;();
/// </code>
/// <para>
/// Both type arguments have to be written out. C# infers all of a method's type arguments or none of them, and
/// the destination is not inferable from anything here, so naming it means naming the source too. For the
/// unpaged case <c>inquiry.Build().ProjectToType&lt;MinionSummary&gt;()</c> says the same thing with one, and is
/// exactly what this does.
/// </para>
/// <para>
/// <b>The order is filter, sort, window, then project</b>, which is the order that makes the window mean
/// anything: paging is over the entity's own sort, and projecting first would leave Mapster's output to be
/// sorted by names the DTO may not even have.
/// </para>
/// <para>
/// <b>The configuration is optional here, unlike the AutoMapper companion.</b> Mapster maps by convention and
/// needs no registration for a DTO whose names line up, so passing nothing uses
/// <see cref="TypeAdapterConfig.GlobalSettings"/>, which is what Mapster's own parameterless overload does. Pass
/// a <see cref="TypeAdapterConfig"/> where the mapping is configured, or where you keep one per scope rather
/// than one per process.
/// </para>
/// </remarks>
public static class InquiryProjection
{
    /// <summary>
    /// Build the query as <see cref="Inquiry{T}.Build"/> does, and read it back as <typeparamref name="TDto"/>.
    /// </summary>
    /// <typeparam name="T">the entity</typeparam>
    /// <typeparam name="TDto">what to read back</typeparam>
    /// <param name="inquiry"></param>
    /// <param name="config">
    /// [OPT] the Mapster configuration holding the mapping. Null takes
    /// <see cref="TypeAdapterConfig.GlobalSettings"/>
    /// </param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">
    /// whatever <see cref="Inquiry{T}.Build"/> would throw, or a projection was applied, see
    /// <see cref="RefuseAppliedProjection"/>
    /// </exception>
    [RequiresDynamicCode("Weequery closes generic types over the property types it binds, which Native AOT cannot generate at runtime")]
    [RequiresUnreferencedCode("Weequery reaches properties by the name a caller gives at runtime, so trimming cannot know which ones to keep")]
    public static IQueryable<TDto> ProjectTo<T, TDto>(this Inquiry<T> inquiry, TypeAdapterConfig? config = null)
        where T : class
    {
        WeequeryException.ThrowIfNull(inquiry);

        RefuseAppliedProjection(inquiry, nameof(ProjectTo));

        var built = inquiry.Build();

        return (config is null) ? built.ProjectToType<TDto>() : built.ProjectToType<TDto>(config);
    }

    /// <summary>
    /// Build both halves as <see cref="Inquiry{T}.BuildPaged"/> does, and read the page back as
    /// <typeparamref name="TDto"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reason this package exists. Writing it by hand is easy to get subtly wrong, because only one of the
    /// two queries should be projected:
    /// <code>
    /// var (page, total) = inquiry.ProjectToPaged&lt;Minion, MinionSummary&gt;();
    ///
    /// var matched = await total.FirstOrDefaultAsync();
    /// var rows    = await page.ToListAsync();
    /// </code>
    /// </para>
    /// <para>
    /// <b>The count is never projected.</b> It counts rows rather than what is read off them, so it asks for
    /// a number and reads no column at all. A DTO whose mapping holds anything Mapster cannot project cannot
    /// fail a count that never needed it, because the count never touches the mapping. See
    /// <see cref="PagedQuery{T}.Total"/> for how to read the number back.
    /// </para>
    /// <para>
    /// Neither query has run, exactly as <see cref="Inquiry{T}.BuildPaged"/> promises.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">the entity</typeparam>
    /// <typeparam name="TDto">what to read the page back as</typeparam>
    /// <param name="inquiry"></param>
    /// <param name="config">
    /// [OPT] the Mapster configuration holding the mapping. Null takes
    /// <see cref="TypeAdapterConfig.GlobalSettings"/>
    /// </param>
    /// <returns>the projected page, and the query counting everything the conditions matched</returns>
    /// <exception cref="WeequeryException">
    /// whatever <see cref="Inquiry{T}.BuildPaged"/> would throw, or a projection was applied
    /// </exception>
    [RequiresDynamicCode("Weequery closes generic types over the property types it binds, which Native AOT cannot generate at runtime")]
    [RequiresUnreferencedCode("Weequery reaches properties by the name a caller gives at runtime, so trimming cannot know which ones to keep")]
    public static PagedQuery<TDto> ProjectToPaged<T, TDto>(this Inquiry<T> inquiry, TypeAdapterConfig? config = null)
        where T : class
    {
        WeequeryException.ThrowIfNull(inquiry);

        RefuseAppliedProjection(inquiry, nameof(ProjectToPaged));

        var paged = inquiry.BuildPaged();

        var page = (config is null) ? paged.Page.ProjectToType<TDto>() : paged.Page.ProjectToType<TDto>(config);

        return new PagedQuery<TDto>(page, paged.Total);
    }

    /// <summary>
    /// Refuse an Inquiry that already had a projection applied.
    /// </summary>
    /// <remarks>
    /// Two answers to one question. <see cref="Inquiry{T}.ApplyProjection(string?, QueryStyle)"/> says a row is the keys the
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

        throw new WeequeryException(WeequeryError.UsageInvalid, $"{called} cannot be used on an Inquiry that has already had ApplyProjection('{inquiry.AppliedProjection.ToQuery()}') called on it, choose one or the other");
    }
}
