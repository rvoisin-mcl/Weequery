namespace Weequery;

/// <summary>
/// One sort clause. Several apply in the order given, each breaking ties in the one before, and the field must be
/// bound, the same as a field in a condition. See <see cref="Inquiry{T}.ApplySort"/>.
/// </summary>
/// <param name="Field">the binding key to sort on, matched without regard to case</param>
/// <param name="Direction">which way round</param>
public record Sort(string Field, SortDirection Direction)
{
    /// <summary>
    /// Read a list of sorts from a text string
    /// </summary>
    /// <remarks>
    /// <para>
    /// The clause is a comma separated list of fields, each optionally followed by a direction, and may begin
    /// with ORDER BY:
    /// </para>
    /// <code>
    /// Pay                              ascending, which is what a field on its own means
    /// Pay DESC
    /// Pay Descending, Name             several, applied in the order written
    /// ORDER BY Pay DESC, Name ASC      the SQL spelling, and the same clause
    /// OrderBy Pay DESC, Name ASC       the one word spelling, and the same clause again
    /// [HireDate] DESC                  a field written the way a condition writes one
    /// </code>
    /// <para>
    /// Asc, Ascending, Desc and Descending are all accepted, without regard to case, as are ORDER BY and
    /// OrderBy. This is separate text from a condition rather than part of one, so the two travel apart.
    /// </para>
    /// <para>
    /// See <see cref="SortParser"/> for the grammar.
    /// </para>
    /// </remarks>
    /// <param name="sortString">the clause; null, empty or whitespace takes <paramref name="defaultSort"/></param>
    /// <param name="defaultSort">
    /// what to sort by when the caller asked for nothing, which is worth supplying wherever the query is paged:
    /// a page of an unordered query holds arbitrary rows, see <see cref="Inquiry{T}.ApplyPagination"/>. Copied,
    /// so the list returned can be changed without changing the default.
    /// </param>
    /// <returns>never null; empty when there was nothing to read and no default was given</returns>
    /// <exception cref="WeequeryException">the clause is malformed</exception>
    public static List<Sort> Parse(string? sortString, IEnumerable<Sort>? defaultSort = null)
    {
        return SortParser.Parse(sortString, defaultSort);
    }

    /// <summary>
    /// Renders in the sort language, so a sort prints as the text it would be written as. See
    /// <see cref="SortFunctions.ToQuery(Sort, QueryStyle)"/>, which this is, and
    /// <see cref="SortFunctions.ToQuery(IEnumerable{Sort}, QueryStyle)"/> for a whole clause.
    /// </summary>
    /// <returns></returns>
    public override string ToString()
    {
        return this.ToQuery();
    }
}
