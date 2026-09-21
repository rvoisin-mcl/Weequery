using Weequery.Interfaces;

namespace Weequery;

// What a caller asks of a query: a condition, a sort, a page, a projection, or a whole request at once. Each
// is recorded against a copy and none of it is composed until Build, see Inquiry.Composition.cs.
public partial class Inquiry<T> where T : class
{
    /// <summary>
    /// Add a condition that will be applied to the query when built. Will be AND'ed with any other root conditions
    /// </summary>
    /// <param name="condition"></param>
    /// <returns></returns>
    public Inquiry<T> ApplyCondition(ICondition? condition)
    {
        if (condition is null) { return this; }

        var next = Copy();

        next.Conditions.Add(condition);

        return next;
    }

    /// <summary>
    /// Parse a query string and add the condition it describes, to be applied when built. Will be AND'ed with any
    /// other root conditions
    /// </summary>
    /// <param name="filterString">eg. "(Pay &gt; 10000) AND NOT (Name StartsWith 'Temp')"</param>
    /// <param name="style">
    /// <see cref="QueryStyle.Native"/> to accept only the one spelling of each operator, so a caller sending
    /// <c>&amp;&amp;</c> or <c>IS NULL</c> is refused and told what to write. Null, the default, accepts every
    /// spelling, which is what this has always done. See <see cref="ConditionFunctions.ParseQuery"/>
    /// </param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">the query is malformed, see <see cref="ConditionFunctions.ParseQuery"/></exception>
    public Inquiry<T> ApplyCondition(string filterString, QueryStyle style = QueryStyle.Native)
    {
        var condition = ConditionFunctions.ParseQuery(filterString, style);
        if (condition is null) { return this; }

        var next = Copy();

        next.Conditions.Add(condition);

        return next;
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

        var next = Copy();

        int index = 0;
        foreach (var condition in conditions)
        {
            if (condition is null) { throw new WeequeryException(WeequeryError.ArgumentMissing, $"{nameof(conditions)}[{index}] is null"); }

            next.Conditions.Add(condition);
            index++;
        }

        return next;
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

        var next = Copy();

        next.Sorts.Add(sort);

        return next;
    }

    /// <summary>
    /// Parse a sort clause and add the sorts it describes, to be applied when built. They will apply in the order
    /// provided
    /// </summary>
    /// <remarks>
    /// The clause is a comma separated list of fields, each optionally followed by a direction.
    /// See <see cref="Sort.Parse"/> for the format
    /// <para>
    /// <paramref name="defaultSort"/> should be supplied wherever the query is paged, since a page of an
    /// unordered query holds arbitrary rows, see <see cref="ApplyPagination"/>.
    /// </para>
    /// </remarks>
    /// <param name="sortString">eg. "Pay DESC, Name". Null, empty or whitespace takes <paramref name="defaultSort"/></param>
    /// <param name="defaultSort">what to sort by when the caller asked for nothing; null, or none, is a NOP</param>
    /// <param name="style">
    /// <see cref="QueryStyle.Native"/> to take only the one word OrderBy prefix, refusing ORDER BY. Null, the
    /// default, takes both. See <see cref="Sort.Parse"/>
    /// </param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">the clause is malformed, see <see cref="Sort.Parse"/></exception>
    public Inquiry<T> ApplySorts(string? sortString, IEnumerable<Sort>? defaultSort = null, QueryStyle style = QueryStyle.Native)
    {
        return ApplySorts(Sort.Parse(sortString, defaultSort, style));
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

        var next = Copy();

        int index = 0;
        foreach (var sort in sorts)
        {
            if (sort is null) { throw new WeequeryException(WeequeryError.ArgumentMissing, $"{nameof(sorts)}[{index}] is null"); }

            WeequeryException.ThrowIfNullOrEmpty(sort.Field, $"{nameof(sorts)}[{index}].{nameof(Sort.Field)}");

            next.Sorts.Add(sort);
            index++;
        }

        return next;
    }

    /// <summary>
    /// Apply paging that will be applied to the query when built
    /// </summary>
    /// <remarks>
    /// <para>
    /// Paging without a unique sort applied will yield undefined output
    /// </para>
    /// <para>
    /// A size and a page that multiply past <see cref="int.MaxValue"/> cannot be resolved, so it is refused.
    /// </para>
    /// </remarks>
    /// <param name="pageSize">
    /// rows per page. A null, or LEQ 0 will be treated as <see cref="InquirySettings.DefaultPageSize"/>; where
    /// no default was set, that is no window and the page index is moot.
    /// </param>
    /// <param name="page">
    /// zero based page index. A negative value will be treated as page 0
    /// </param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">
    /// <paramref name="page"/> and a size the caller named combine past <see cref="int.MaxValue"/> rows to skip
    /// </exception>
    public Inquiry<T> ApplyPagination(int? pageSize, int page)
    {
        // bound page and pageSize
        int size = ((pageSize is int named) && (named > 0)) ? named : UnsetPageSize;
        int index = (page > 0) ? page : 0;

        // Check if the combined values will overflow an int
        if ((size > 0) && ((long)size * index > int.MaxValue))
        {
            throw new WeequeryException(WeequeryError.ArgumentInvalid, $"{nameof(pageSize)} {size} * {nameof(page)} {index} exceeds {int.MaxValue}");
        }

        var next = Copy();

        next.PageSize = size;
        next.Page = index;

        return next;
    }
    /// <summary>
    /// Read back only the fields named, rather than the whole entity. See <see cref="BuildProjected"/>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <code>
    /// var rows = query.WithWeequery()
    ///     .BindProperties(MinionBindings)
    ///     .ApplyCondition("IsActive = true")
    ///     .ApplyProjection("Name, Pay")
    ///     .BuildProjected();
    /// </code>
    /// </para>
    /// <para>
    /// If called multiple times, the last call wins.
    /// </para>
    /// </remarks>
    /// <param name="projectionString">
    /// a comma separated list of keys, each written as a binding key and each able to carry an
    /// index: "Name, Pay, Tallies[apples]". An empty list clears any projection already applied, a key 
    /// named twice is kept once
    /// </param>
    /// <param name="style"></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">the list is malformed</exception>
    public Inquiry<T> ApplyProjection(string? projectionString, QueryStyle style = QueryStyle.Native)
    {
        return ApplyProjection(Projection.Parse(projectionString, style));
    }

    /// <summary>
    /// Read back only the fields named, rather than the whole entity. See <see cref="BuildProjected"/>
    /// </summary>
    /// <param name="keys">An empty list clears any projection already applied; a key named twice is kept once</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">a key is null or empty</exception>
    public Inquiry<T> ApplyProjection(IEnumerable<string>? keys)
    {
        return ApplyProjection(Projection.Of(keys));
    }

    /// <summary>
    /// Read back only the fields named from a projection already applied
    /// </summary>
    /// <param name="projection">null or <see cref="Projection.None"/> clears any projection already applied</param>
    /// <returns></returns>
    public Inquiry<T> ApplyProjection(Projection? projection)
    {
        var next = Copy();

        next.Projected = projection ?? Projection.None;

        return next;
    }

    /// <summary>
    /// Apply a whole request at once: its condition, its sorts, the fields it wants read back and the page of
    /// them it asked for. See <see cref="QueryRequest"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The four calls a request handler was going to make anyway, in the order they have to happen, over a type
    /// that binds straight off a query string.
    /// <code>
    /// var (page, total) = _context.Minions
    ///     .WithWeequery()
    ///     .BindProperties(MinionBindings)
    ///     .ApplyRequest(request, DefaultSort)
    ///     .BuildPagedProjected();
    /// </code>
    /// </para>
    /// <para>
    /// <b>It refuses as its components refuse.</b> Expected errors will be identical to calling the same functions with
    /// the same data. <see cref="Validate(QueryRequest, IEnumerable{Sort}?)"/> will report any the errors 
    /// in the same place and is worth requesting first if the request came from outside.
    /// </para>
    /// </remarks>
    /// <param name="request">the caller's query; null is a NOP</param>
    /// <param name="defaultSort">
    /// what to sort by where the request named no sorts. See <see cref="ApplyPagination"/>
    /// </param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">
    /// any part of the request is malformed, spells something a way <see cref="QueryStyle.Native"/> refuses,
    /// or its paging is out of range
    /// </exception>
    public Inquiry<T> ApplyRequest(QueryRequest? request, IEnumerable<Sort>? defaultSort = null)
    {
        if (request is null) { return this; }

        // Read once rather than a part at a time, so the one string is parsed once, see QueryRequest.Unpack
        var unpacked = request.Unpack(defaultSort);

        return ApplyCondition(unpacked.Condition)
            .ApplySorts(unpacked.Sorts)
            .ApplyProjection(unpacked.Projection)
            .ApplyPagination(request.PageSize, request.Page ?? 0);
    }

}
