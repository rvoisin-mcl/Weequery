namespace Weequery;

/// <summary>
/// Writes a sort clause back out as text, so that feeding the result to <see cref="Sort.Parse"/> produces the
/// same sorts. The inverse of the parser, and the counterpart of <see cref="ConditionFunctions.ToQuery"/>.
/// </summary>
public static class SortFunctions
{
    /// <summary>
    /// Write one sort
    /// </summary>
    /// <param name="sort"></param>
    /// <param name="style">
    /// only decides the prefix, since a sort has no operator with two spellings. See the overload taking several.
    /// </param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">the sort is null, or names no field</exception>
    public static string ToQuery(this Sort sort, QueryStyle style = QueryStyle.CSharp)
    {
        if (sort is null) { throw new WeequeryException($"{nameof(sort)} cannot be null"); }

        WeequeryException.ThrowIfNullOrEmpty(sort.Field, $"{nameof(sort)}.{nameof(Sort.Field)}");

        return $"{Prefix(style)}{Clause(sort)}";
    }

    /// <summary>
    /// Write a whole clause, in the order the sorts apply.
    /// </summary>
    /// <param name="sorts">null, or none, gives the empty string, which reads back as no sorts at all</param>
    /// <param name="style">
    /// <see cref="QueryStyle.Sql"/> writes the ORDER BY the parser will accept but does not require;
    /// <see cref="QueryStyle.CSharp"/> leaves it off, since nothing needs it to read the clause back
    /// </param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">one of the sorts is null, or names no field</exception>
    public static string ToQuery(this IEnumerable<Sort>? sorts, QueryStyle style = QueryStyle.CSharp)
    {
        if (sorts is null) { return string.Empty; }

        List<string> clauses = new();
        var index = 0;

        foreach (var sort in sorts)
        {
            if (sort is null) { throw new WeequeryException($"{nameof(sorts)}[{index}] is null"); }

            WeequeryException.ThrowIfNullOrEmpty(sort.Field, $"{nameof(sorts)}[{index}].{nameof(Sort.Field)}");

            clauses.Add(Clause(sort));
            index++;
        }

        // Don't just return 'OrderBy'
        if (clauses.Count == 0) { return string.Empty; }

        return $"{Prefix(style)}{string.Join(", ", clauses)}";
    }

    /// <summary>
    /// One field and its direction, without the prefix
    /// </summary>
    private static string Clause(Sort sort)
    {
        return $"{QueryWriter.Field(sort.Field)} {Spelling(sort.Direction)}";
    }

    /// <summary>
    /// What the clause opens with, which is the only thing a style decides here
    /// </summary>
    private static string Prefix(QueryStyle style)
    {
        return (style == QueryStyle.Sql) ? "ORDER BY " : string.Empty;
    }

    /// <summary>
    /// The one spelling a direction is written as. The parser reads the long forms too, but writing settles on
    /// the short one so there is a single form to compare against.
    /// </summary>
    /// <param name="direction"></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException"></exception>
    private static string Spelling(SortDirection direction)
    {
        return direction switch
        {
            SortDirection.Ascending => "ASC",
            SortDirection.Descending => "DESC",

            _ => throw new WeequeryException($"SortDirection {direction} is invalid"),
        };
    }
}
