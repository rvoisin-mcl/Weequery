using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using Weequery.Interfaces;

namespace Weequery.Elasticsearch;

/// <summary>
/// A whole search body: the condition as a query, the sorts, the window, and the projection as
/// <c>_source</c> filtering.
/// </summary>
/// <remarks>
/// <para>
/// Everything an <see cref="Inquiry{T}"/> applies to an <see cref="IQueryable{T}"/>, applied instead to what you
/// POST at <c>_search</c>:
/// </para>
/// <code>
/// var parsed = ParsedQuery.Parse(request.Query);
///
/// var body = ElasticSearchBody.Build(fields, parsed.Condition, parsed.Sorts,
///     Projection.Parse(request.Fields), request.PageSize, request.Page);
///
/// // { "query": { ... }, "sort": [ ... ], "from": 40, "size": 20, "_source": { "includes": [ ... ] } }
/// </code>
/// <para>
/// The same allow-list decides all four, so a caller can no more sort or read a field they were not given than
/// they can filter on one.
/// </para>
/// </remarks>
public static class ElasticSearchBody
{
    /// <summary>
    /// Assemble a search body.
    /// </summary>
    /// <param name="fields">the allow-list</param>
    /// <param name="condition">[OPT] null gives <c>match_all</c>, which is what no filtering means</param>
    /// <param name="sorts">[OPT] in the order they apply, each breaking ties in the one before</param>
    /// <param name="projection">
    /// [OPT] which fields to read back, as <c>_source.includes</c>. <see cref="Projection.None"/> leaves
    /// <c>_source</c> off entirely, which is Elasticsearch's own "all of it"
    /// </param>
    /// <param name="pageSize">[OPT] the window's size; anything above zero adds <c>size</c></param>
    /// <param name="page">[OPT] zero based, which becomes <c>from</c> once multiplied out</param>
    /// <returns>a fresh object every call, safe to add your own members to</returns>
    /// <exception cref="WeequeryException">
    /// whatever <see cref="ElasticQuery.Build(ICondition, ElasticFieldSet)"/> would throw, a sort or a projected field is not declared, or the
    /// window is out of range
    /// </exception>
    [RequiresDynamicCode("An Elasticsearch query is built as System.Text.Json nodes, which reflect over the values they are given. Use the source generator, and give it the value types these conditions carry")]
    [RequiresUnreferencedCode("An Elasticsearch query is built as System.Text.Json nodes, which reflect over the values they are given. Use the source generator, and give it the value types these conditions carry")]
    public static JsonObject Build(
        ElasticFieldSet fields,
        ICondition? condition = null,
        IEnumerable<Sort>? sorts = null,
        Projection? projection = null,
        int pageSize = -1,
        int page = 0)
    {
        WeequeryException.ThrowIfNull(fields);

        var body = new JsonObject { ["query"] = ElasticQuery.Build(condition, fields) };

        var order = Sorts(sorts, fields);
        if (order.Count > 0) { body["sort"] = order; }

        Window(body, pageSize, page);

        var includes = Includes(projection, fields);
        if (includes is not null) { body["_source"] = new JsonObject { ["includes"] = includes }; }

        return body;
    }

    /// <summary>
    /// The same, as JSON text
    /// </summary>
    /// <param name="fields"></param>
    /// <param name="condition">[OPT]</param>
    /// <param name="sorts">[OPT]</param>
    /// <param name="projection">[OPT]</param>
    /// <param name="pageSize">[OPT]</param>
    /// <param name="page">[OPT]</param>
    /// <param name="indented">[OPT] true to write it readably, for a log or a test</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">whatever <see cref="Build"/> would throw</exception>
    [RequiresDynamicCode("An Elasticsearch query is built as System.Text.Json nodes, which reflect over the values they are given. Use the source generator, and give it the value types these conditions carry")]
    [RequiresUnreferencedCode("An Elasticsearch query is built as System.Text.Json nodes, which reflect over the values they are given. Use the source generator, and give it the value types these conditions carry")]
    public static string ToJson(
        ElasticFieldSet fields,
        ICondition? condition = null,
        IEnumerable<Sort>? sorts = null,
        Projection? projection = null,
        int pageSize = -1,
        int page = 0,
        bool indented = false)
    {
        return Build(fields, condition, sorts, projection, pageSize, page)
            .ToJsonString(new JsonSerializerOptions { WriteIndented = indented });
    }

    /// <summary>
    /// The sort clause, in the order given.
    /// </summary>
    /// <remarks>
    /// A field inside a nested path is refused. Sorting on one needs a <c>nested</c> sort option saying which of
    /// the many values to sort by, and there is nothing in a Weequery sort that says which, so guessing would
    /// silently pick one.
    /// </remarks>
    [RequiresDynamicCode("An Elasticsearch query is built as System.Text.Json nodes, which reflect over the values they are given. Use the source generator, and give it the value types these conditions carry")]
    [RequiresUnreferencedCode("An Elasticsearch query is built as System.Text.Json nodes, which reflect over the values they are given. Use the source generator, and give it the value types these conditions carry")]
    private static JsonArray Sorts(IEnumerable<Sort>? sorts, ElasticFieldSet fields)
    {
        var order = new JsonArray();

        foreach (var sort in sorts ?? [])
        {
            var field = fields.Resolve(sort.Field, "sorted on");

            if (field.Nested is not null)
            {
                throw new WeequeryException(WeequeryError.OperatorUnsupported, $"Cannot sort on '{field.Key}', which is declared under the nested path '{field.Nested}'");
            }

            var direction = (sort.Direction == SortDirection.Descending) ? "desc" : "asc";

            order.Add(new JsonObject { [field.Field] = new JsonObject { ["order"] = direction } });
        }

        return order;
    }

    /// <summary>
    /// The window, as Elasticsearch spells it.
    /// </summary>
    /// <remarks>
    /// <c>from</c> and <c>size</c> rather than skip and take, and the same arithmetic. Deep paging this way runs
    /// into <c>index.max_result_window</c>, ten thousand by default, which is the index's setting to raise or a
    /// reason to reach for <c>search_after</c>; either way it is not something this can decide.
    /// </remarks>
    private static void Window(JsonObject body, int pageSize, int page)
    {
        if (pageSize <= 0) { return; }

        if (page < 0) { throw new WeequeryException(WeequeryError.ArgumentInvalid, $"{nameof(page)} must be >= 0"); }

        long from = (long)pageSize * page;

        if (from > int.MaxValue)
        {
            throw new WeequeryException(WeequeryError.ArgumentInvalid, $"{nameof(pageSize)} {pageSize} * {nameof(page)} {page} exceeds {int.MaxValue}");
        }

        body["from"] = (int)from;
        body["size"] = pageSize;
    }

    /// <summary>
    /// The projected fields as index field names, or null where nothing was projected.
    /// </summary>
    /// <remarks>
    /// <c>_source</c> filtering is not the same trade as a narrower SELECT: Elasticsearch reads the whole
    /// <c>_source</c> and hands back part of it, so this saves the bytes on the wire rather than the read. Worth
    /// having, and worth knowing it is not the same saving.
    /// </remarks>
    [RequiresDynamicCode("An Elasticsearch query is built as System.Text.Json nodes, which reflect over the values they are given. Use the source generator, and give it the value types these conditions carry")]
    [RequiresUnreferencedCode("An Elasticsearch query is built as System.Text.Json nodes, which reflect over the values they are given. Use the source generator, and give it the value types these conditions carry")]
    private static JsonArray? Includes(Projection? projection, ElasticFieldSet fields)
    {
        if ((projection is null) || projection.IsEmpty) { return null; }

        var includes = new JsonArray();

        foreach (var field in fields.Project(projection, "read back"))
        {
            // JsonValue.Create rather than Add(string): the generic Add<T> overload wraps it as a
            // JsonValueCustomized<string>, which cannot be written without a TypeInfoResolver on the options,
            // and this package deliberately passes none. Primitive in, no serializer needed to get it out
            includes.Add(JsonValue.Create(field.Field));
        }

        return includes;
    }
}
