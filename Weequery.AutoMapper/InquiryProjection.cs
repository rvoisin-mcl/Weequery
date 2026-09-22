using AutoMapper;
using AutoMapper.QueryableExtensions;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace Weequery.AutoMapper;

/// <summary>
/// Reads the rows of an <see cref="Inquiry{T}"/> back as your own type, through AutoMapper.
/// </summary>
/// <remarks>
/// <para>
/// Weequery decides <b>which rows</b>: the conditions, the sorts and the window, all against the entity and its
/// allow-list. AutoMapper decides <b>what a row looks like</b>: the DTO's own mapping, projected into the query
/// so only the columns it needs leave the database.
/// </para>
/// <code>
/// var page = context.Minions
///     .WithWeequery()
///     .BindProperties(MinionBindings)
///     .ApplyCondition(request.Filter)
///     .ApplySorts(request.Sort, DefaultSort)
///     .ApplyPagination(request.PageSize, request.Page)
///     .ProjectTo&lt;Minion, MinionSummary&gt;(configuration);
/// </code>
/// <para>
/// Both type arguments have to be written out. C# infers all of a method's type arguments or none of them, and
/// the destination is not inferable from anything here, so naming it means naming the source too. For the
/// unpaged case <c>inquiry.Build().ProjectTo&lt;MinionSummary&gt;(configuration)</c> says the same thing with one,
/// and is exactly what this does.
/// </para>
/// <para>
/// <b>The order is filter, sort, window, then project</b>, which is the order that makes the window mean
/// anything: paging is over the entity's own sort, and projecting first would leave AutoMapper's output to be
/// sorted by names the DTO may not even have.
/// </para>
/// </remarks>
public static class InquiryProjection
{
    /// <summary>
    /// Build the query as <see cref="Inquiry{T}.Build"/> does, and read it back as <typeparamref name="TDto"/>.
    /// </summary>
    /// <typeparam name="T">the entity</typeparam>
    /// <typeparam name="TDto">what to read back, which AutoMapper must have a map to</typeparam>
    /// <param name="inquiry"></param>
    /// <param name="configuration">the AutoMapper configuration holding the map</param>
    /// <param name="membersToExpand">[OPT] members to expand, as AutoMapper's own overload takes them</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">
    /// whatever <see cref="Inquiry{T}.Build"/> would throw, or a projection was applied, see
    /// <see cref="RefuseAppliedProjection"/>
    /// </exception>
    [RequiresDynamicCode("Weequery closes generic types over the property types it binds, which Native AOT cannot generate at runtime")]
    [RequiresUnreferencedCode("Weequery reaches properties by the name a caller gives at runtime, so trimming cannot know which ones to keep")]
    public static IQueryable<TDto> ProjectTo<T, TDto>(
        this Inquiry<T> inquiry,
        IConfigurationProvider configuration,
        params Expression<Func<TDto, object>>[] membersToExpand)
        where T : class
    {
        WeequeryException.ThrowIfNull(inquiry);
        WeequeryException.ThrowIfNull(configuration);

        RefuseAppliedProjection(inquiry, nameof(ProjectTo));

        return inquiry.Build().ProjectTo(configuration, membersToExpand);
    }

    /// <summary>
    /// The same, from a mapper rather than from its configuration, for the caller who has the one injected and
    /// not the other.
    /// </summary>
    /// <typeparam name="T">the entity</typeparam>
    /// <typeparam name="TDto">what to read back, which AutoMapper must have a map to</typeparam>
    /// <param name="inquiry"></param>
    /// <param name="mapper"></param>
    /// <param name="membersToExpand">[OPT] members to expand, as AutoMapper's own overload takes them</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">whatever <see cref="ProjectTo{T, TDto}(Inquiry{T}, IConfigurationProvider, Expression{Func{TDto, object}}[])"/> would throw</exception>
    [RequiresDynamicCode("Weequery closes generic types over the property types it binds, which Native AOT cannot generate at runtime")]
    [RequiresUnreferencedCode("Weequery reaches properties by the name a caller gives at runtime, so trimming cannot know which ones to keep")]
    public static IQueryable<TDto> ProjectTo<T, TDto>(
        this Inquiry<T> inquiry,
        IMapper mapper,
        params Expression<Func<TDto, object>>[] membersToExpand)
        where T : class
    {
        WeequeryException.ThrowIfNull(mapper);

        return inquiry.ProjectTo(mapper.ConfigurationProvider, membersToExpand);
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
    /// var (page, total) = inquiry.ProjectToPaged&lt;Minion, MinionSummary&gt;(configuration);
    ///
    /// var matched = await total.FirstOrDefaultAsync();
    /// var rows    = await page.ToListAsync();
    /// </code>
    /// </para>
    /// <para>
    /// <b>The count is never projected.</b> It counts rows rather than what is read off them, so it asks for
    /// a number and reads no column at all. A DTO whose mapping holds anything AutoMapper cannot project cannot
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
    /// <param name="configuration">the AutoMapper configuration holding the map</param>
    /// <param name="membersToExpand">[OPT] members to expand, as AutoMapper's own overload takes them</param>
    /// <returns>the projected page, and the query counting everything the conditions matched</returns>
    /// <exception cref="WeequeryException">
    /// whatever <see cref="Inquiry{T}.BuildPaged"/> would throw, or a projection was applied
    /// </exception>
    [RequiresDynamicCode("Weequery closes generic types over the property types it binds, which Native AOT cannot generate at runtime")]
    [RequiresUnreferencedCode("Weequery reaches properties by the name a caller gives at runtime, so trimming cannot know which ones to keep")]
    public static PagedQuery<TDto> ProjectToPaged<T, TDto>(
        this Inquiry<T> inquiry,
        IConfigurationProvider configuration,
        params Expression<Func<TDto, object>>[] membersToExpand)
        where T : class
    {
        WeequeryException.ThrowIfNull(inquiry);
        WeequeryException.ThrowIfNull(configuration);

        RefuseAppliedProjection(inquiry, nameof(ProjectToPaged));

        var paged = inquiry.BuildPaged();

        return new PagedQuery<TDto>(paged.Page.ProjectTo(configuration, membersToExpand), paged.Total);
    }

    /// <summary>
    /// The same, from a mapper rather than from its configuration.
    /// </summary>
    /// <typeparam name="T">the entity</typeparam>
    /// <typeparam name="TDto">what to read the page back as</typeparam>
    /// <param name="inquiry"></param>
    /// <param name="mapper"></param>
    /// <param name="membersToExpand">[OPT] members to expand, as AutoMapper's own overload takes them</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">whatever the configuration overload would throw</exception>
    [RequiresDynamicCode("Weequery closes generic types over the property types it binds, which Native AOT cannot generate at runtime")]
    [RequiresUnreferencedCode("Weequery reaches properties by the name a caller gives at runtime, so trimming cannot know which ones to keep")]
    public static PagedQuery<TDto> ProjectToPaged<T, TDto>(
        this Inquiry<T> inquiry,
        IMapper mapper,
        params Expression<Func<TDto, object>>[] membersToExpand)
        where T : class
    {
        WeequeryException.ThrowIfNull(mapper);

        return inquiry.ProjectToPaged<T, TDto>(mapper.ConfigurationProvider, membersToExpand);
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

        throw new WeequeryException(WeequeryError.UsageInvalid, $"{called} cannot be used on an Inquiry that has already had ApplyProjection('{inquiry.AppliedProjection.ToQuery()}') called on it, use one or the other.");
    }
}
