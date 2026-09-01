using Weequery.Interfaces;

namespace Weequery;

/// <summary>
/// A condition and a sort clause read from one string, for the callers that would rather send one than two.
/// </summary>
/// <remarks>
/// <para>
/// Deconstructs, so the two come apart where they are used:
/// </para>
/// <code>
/// var (condition, sorts) = ParsedQuery.Parse("Pay > 10000 ORDER BY Pay DESC");
/// </code>
/// </remarks>
/// <param name="Condition">what to filter by, or null where the string asked for no filtering</param>
/// <param name="Sorts">what to sort by, in the order they apply; never null, and empty where nothing was asked</param>
public record ParsedQuery(ICondition? Condition, List<Sort> Sorts)
{
    /// <summary>
    /// Read a condition and a sort clause from one string, the two separated by ORDER BY or OrderBy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The separator is what tells the two apart, so unlike the clause <see cref="Sort.Parse"/> reads on its own,
    /// here it is required to introduce sorts. Without one the whole string is a condition. Either half may be
    /// left out:
    /// </para>
    /// <code>
    /// Pay > 10000 ORDER BY Pay DESC, Name     both
    /// Pay > 10000 OrderBy Pay DESC            both, the one word spelling
    /// Pay > 10000                             a condition, and whatever default sort was given
    /// ORDER BY Pay DESC                       sorts, and no filtering at all
    /// </code>
    /// <para>
    /// Where the split falls is found by reading the condition and seeing where it stops, not by searching the
    /// text, so a value that spells the separator is still a value: <c>Name == 'ORDER BY'</c> is one comparison
    /// and no sorts. See <see cref="QueryParser.ParseLeading"/>.
    /// </para>
    /// <para>
    /// A bare OrderBy at the very front is read as the separator rather than as a field, which is why no binding
    /// may be named for one, see <see cref="QueryKeywords"/>. ORDER and BY are read only together and so are not
    /// reserved: a field named Order is filtered and sorted on like any other.
    /// </para>
    /// </remarks>
    /// <param name="query">null, empty or whitespace gives no condition and <paramref name="defaultSort"/></param>
    /// <param name="defaultSort">
    /// what to sort by where the string named no sorts, copied rather than kept. Worth supplying wherever the
    /// query is paged, see <see cref="Inquiry{T}.ApplyPagination"/>.
    /// </param>
    /// <returns>never null, though both halves of it may be empty</returns>
    /// <exception cref="WeequeryException">either half is malformed</exception>
    public static ParsedQuery Parse(string? query, IEnumerable<Sort>? defaultSort = null)
    {
        var tokens = QueryTokenizer.Tokenize(query ?? string.Empty);

        if (tokens.Count == 0) { return new ParsedQuery(null, [.. defaultSort ?? []]); }

        // No condition(s) found, only (presumable) ordering
        if (SortParser.PrefixLength(tokens, 0) > 0) { return new ParsedQuery(null, Sort.Parse(query, defaultSort)); }

        var condition = QueryParser.ParseLeading(tokens, query!, out var stopped);

        // (Presumable) condition(s) found, but no ordering
        if (stopped >= tokens.Count) { return new ParsedQuery(condition, [.. defaultSort ?? []]); }

        // Check for unexpected text between the condition text and the ordering text
        if (SortParser.PrefixLength(tokens, stopped) == 0)
        {
            throw new WeequeryException(QueryText.Describe(query!, $"Unexpected '{tokens[stopped].Text}'", tokens[stopped].Position));
        }

        // Condition(s) found, ordering found
        return new ParsedQuery(condition, Sort.Parse(query![tokens[stopped].Position..], defaultSort));
    }

    /// <summary>
    /// Write both halves back out as one string, such that <see cref="Parse"/> reads it back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Either half may be missing, and neither leaves anything dangling: no sorts writes the condition alone, no
    /// condition writes the clause alone, and neither writes the empty string.
    /// </para>
    /// </remarks>
    /// <param name="style">
    /// which spelling the condition uses for the operators that have two. It does not reach the sorts, which
    /// have none, nor the separator, which is required either way
    /// </param>
    /// <returns>the empty string where there is neither a condition nor a sort</returns>
    /// <exception cref="WeequeryException">
    /// the condition cannot be written, or a sort names no field. See <see cref="ConditionFunctions.ToQuery"/>
    /// and <see cref="SortFunctions.ToQuery(IEnumerable{Sort}, QueryStyle)"/>
    /// </exception>
    public string ToQuery(QueryStyle style = QueryStyle.CSharp)
    {
        // Always the SQL style for the sorts, since that is the one that writes the separator. An empty list
        // gives the empty string rather than a bare prefix, so this is also the test for having any.
        var sorts = Sorts.ToQuery(QueryStyle.Sql);

        if (Condition is null) { return sorts; }

        var condition = Condition.ToQuery(style);

        return (sorts.Length == 0) ? condition : $"{condition} {sorts}";
    }

    /// <summary>
    /// Renders as the text it would be written as, see <see cref="ToQuery"/>
    /// </summary>
    /// <returns></returns>
    public override string ToString()
    {
        return ToQuery();
    }
}
