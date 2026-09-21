using Weequery.Interfaces;

namespace Weequery.OData;

/// <summary>
/// The whole set of query options: the condition as <c>$filter</c>, the sorts as <c>$orderby</c>, the window as
/// <c>$top</c> and <c>$skip</c>, and the projection as <c>$select</c>.
/// </summary>
/// <remarks>
/// <para>
/// Everything an <see cref="Inquiry{T}"/> applies to an <see cref="IQueryable{T}"/>, applied instead to a URL:
/// </para>
/// <code>
/// var parsed = ParsedQuery.Parse(request.Query);
///
/// var options = ODataQuery.Build(fields, parsed.Condition, parsed.Sorts,
///     Projection.Parse(request.Fields), request.PageSize, request.Page);
///
/// // $filter=Active eq true&amp;$orderby=Salary desc&amp;$top=20&amp;$skip=40&amp;$select=Name,Alias
/// </code>
/// <para>
/// The same allow-list decides all four, so a caller can no more sort or read a field they were not given than
/// they can filter on one.
/// </para>
/// <para>
/// <b>Nothing here is percent encoded.</b> The options come back as their own values, and
/// <see cref="ToQueryString"/> joins them raw. Encoding is the job of whatever builds the URL, and doing it here
/// would mean a caller who does it properly encodes it twice.
/// </para>
/// </remarks>
public static class ODataQuery
{
    /// <summary>
    /// The query options, each under its own name, and only the ones that were asked for.
    /// </summary>
    /// <param name="fields">the allow-list</param>
    /// <param name="condition">[OPT] null, or one that writes to nothing, leaves <c>$filter</c> off</param>
    /// <param name="sorts">[OPT] in the order they apply, each breaking ties in the one before</param>
    /// <param name="projection">[OPT] which properties to read back, as <c>$select</c></param>
    /// <param name="pageSize">[OPT] anything above zero adds <c>$top</c>, and <c>$skip</c> where the page is not the first</param>
    /// <param name="page">[OPT] zero based, which becomes <c>$skip</c> once multiplied out</param>
    /// <param name="version">[OPT] which revision to write for, 4.01 by default</param>
    /// <returns>never null, and empty where nothing at all was asked</returns>
    /// <exception cref="WeequeryException">
    /// whatever <see cref="ODataFilter.Write(ICondition, ODataFieldSet, ODataVersion)"/> would throw, a sort or a selected field is not declared, or the
    /// window is out of range
    /// </exception>
    public static Dictionary<string, string> Build(
        ODataFieldSet fields,
        ICondition? condition = null,
        IEnumerable<Sort>? sorts = null,
        Projection? projection = null,
        int pageSize = -1,
        int page = 0,
        ODataVersion version = ODataVersion.V401)
    {
        WeequeryException.ThrowIfNull(fields);

        var options = new Dictionary<string, string>(StringComparer.Ordinal);

        var filter = ODataFilter.Write(condition, fields, version);
        if (filter.Length > 0) { options["$filter"] = filter; }

        var order = OrderBy(sorts, fields);
        if (order.Length > 0) { options["$orderby"] = order; }

        Window(options, pageSize, page);

        var select = Select(projection, fields);
        if (select.Length > 0) { options["$select"] = select; }

        return options;
    }

    /// <summary>
    /// The same, joined into a query string.
    /// </summary>
    /// <remarks>
    /// Raw, in the order OData documents them, and with no leading "?" so it appends to whatever you already
    /// have. Percent encode the values before this reaches a URL, see the remarks on <see cref="ODataQuery"/>.
    /// </remarks>
    /// <param name="fields"></param>
    /// <param name="condition">[OPT]</param>
    /// <param name="sorts">[OPT]</param>
    /// <param name="projection">[OPT]</param>
    /// <param name="pageSize">[OPT]</param>
    /// <param name="page">[OPT]</param>
    /// <param name="version">[OPT]</param>
    /// <returns>the empty string where nothing at all was asked</returns>
    /// <exception cref="WeequeryException">whatever <see cref="Build"/> would throw</exception>
    public static string ToQueryString(
        ODataFieldSet fields,
        ICondition? condition = null,
        IEnumerable<Sort>? sorts = null,
        Projection? projection = null,
        int pageSize = -1,
        int page = 0,
        ODataVersion version = ODataVersion.V401)
    {
        var options = Build(fields, condition, sorts, projection, pageSize, page, version);

        // A fixed order rather than the dictionary's, so the same query gives the same string every time, which
        // is what makes one comparable, cacheable and worth putting in a test
        string[] order = ["$filter", "$orderby", "$top", "$skip", "$select"];

        return string.Join("&", from name in order where options.ContainsKey(name) select $"{name}={options[name]}");
    }

    /// <summary>
    /// The sort clause. A field inside a collection is refused: <c>$orderby</c> addresses the entity, and there
    /// is nothing in a Weequery sort saying which of a collection's many values to order by.
    /// </summary>
    private static string OrderBy(IEnumerable<Sort>? sorts, ODataFieldSet fields)
    {
        var clauses = new List<string>();

        foreach (var sort in sorts ?? [])
        {
            var field = fields.Resolve(sort.Field, "sorted on");

            if (field.Collection is not null)
            {
                throw new WeequeryException(WeequeryError.OperatorUnsupported, $"Cannot sort on '{field.Key}', which is declared inside the collection '{field.Collection}': $orderby has to say which of the many values to order by, and a sort does not carry that");
            }

            if (field.Kind == ODataFieldKind.Collection)
            {
                throw new WeequeryException(WeequeryError.OperatorUnsupported, $"Cannot sort on '{field.Key}', which is a collection and has no single value to order by");
            }

            // Ascending is the default and is written anyway, since a clause that says which way it runs is one
            // fewer thing to know when reading a URL back
            clauses.Add($"{field.Field} {((sort.Direction == SortDirection.Descending) ? "desc" : "asc")}");
        }

        return string.Join(",", clauses);
    }

    /// <summary>
    /// The window, as OData spells it. <c>$top</c> and <c>$skip</c> rather than take and skip, and the same
    /// arithmetic; a service may cap <c>$top</c> at its own maximum page size whatever is asked for.
    /// </summary>
    private static void Window(Dictionary<string, string> options, int pageSize, int page)
    {
        if (pageSize <= 0) { return; }

        if (page < 0) { throw new WeequeryException(WeequeryError.ArgumentInvalid, $"{nameof(page)} must be >= 0"); }

        long skip = (long)pageSize * page;

        if (skip > int.MaxValue)
        {
            throw new WeequeryException(WeequeryError.ArgumentInvalid, $"{nameof(pageSize)} {pageSize} * {nameof(page)} {page} exceeds {int.MaxValue}");
        }

        options["$top"] = pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Left off for the first page, where skipping nothing is what not saying so already means
        if (skip > 0) { options["$skip"] = skip.ToString(System.Globalization.CultureInfo.InvariantCulture); }
    }

    /// <summary>
    /// The projected fields as property paths.
    /// </summary>
    /// <remarks>
    /// <c>$select</c> takes properties of the entity, so a field declared inside a collection is refused: reading
    /// one of those is <c>$expand</c> with its own nested select, which is more than a flat list of keys says.
    /// </remarks>
    private static string Select(Projection? projection, ODataFieldSet fields)
    {
        if ((projection is null) || projection.IsEmpty) { return string.Empty; }

        var selected = new List<string>();

        foreach (var field in fields.Project(projection, "read back"))
        {
            if (field.Collection is not null)
            {
                throw new WeequeryException(WeequeryError.OperatorUnsupported, $"Cannot select '{field.Key}', which is declared inside the collection '{field.Collection}': reading one of those is $expand with a select of its own");
            }

            selected.Add(field.Field);
        }

        return string.Join(",", selected);
    }
}
