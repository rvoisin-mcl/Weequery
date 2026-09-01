namespace Weequery;

/// <summary>
/// Reads a sort clause. The grammar is:
/// <code>
/// sorts     := ('ORDER' 'BY' | 'ORDERBY')? sort (',' sort)*
/// sort      := field direction?
/// field     := WORD | QUOTED | '[' WORD ']'
/// direction := 'ASC' | 'ASCENDING' | 'DESC' | 'DESCENDING'
/// </code>
/// So "Pay DESC, Name", "ORDER BY Pay DESC, Name" and "OrderBy Pay DESC, Name" are all the same clause, and a
/// field written without a direction sorts ascending, as it does in SQL.
/// <para>
/// A field is written the way a condition writes one, see <see cref="QueryParser"/>: bare, quoted, or between
/// brackets, so a key that needs quoting reads the same in both. Everything is matched without regard to case.
/// </para>
/// <para>
/// This is separate text from a condition rather than a clause appended to one, so a caller sends the two apart
/// and neither has to know about the other.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// Not recursive, so unlike <see cref="QueryParser"/> there is no depth to bound: a sort clause is a flat list
/// however long it runs.
/// </para>
/// <para>
/// The prefix is optional, unless within a combined Condition+Ordering string. A field
/// really named Order still works, since "Order DESC" is a field followed by a direction and only "ORDER BY"
/// together is the prefix; and a field named Desc still works, since a direction is read only after a field has
/// been.
/// </para>
/// </remarks>
internal sealed class SortParser
{
    private static readonly Dictionary<string, SortDirection> DirectionLookup = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Asc", SortDirection.Ascending },
        { "Ascending", SortDirection.Ascending },
        { "Desc", SortDirection.Descending },
        { "Descending", SortDirection.Descending },
    };

    private readonly List<QueryToken> Tokens;
    private readonly string Sorts;
    private int Index;

    private SortParser(List<QueryToken> tokens, string sorts)
    {
        Tokens = tokens;
        Sorts = sorts;
    }

    /// <summary>
    /// Read a sort clause, falling back to the sorts the caller decided on where there is nothing to read.
    /// </summary>
    /// <param name="sortString">null, empty or whitespace to take the default</param>
    /// <param name="defaultSort">what to sort by when the caller asked for nothing, copied rather than kept</param>
    /// <returns>never null; empty when there was nothing to read and no default</returns>
    /// <exception cref="WeequeryException">the clause is malformed</exception>
    public static List<Sort> Parse(string? sortString, IEnumerable<Sort>? defaultSort)
    {
        var tokens = QueryTokenizer.Tokenize(sortString ?? string.Empty);

        // Nothing asked for, return the default
        if (tokens.Count == 0) { return [.. defaultSort ?? []]; }

        var parser = new SortParser(tokens, sortString!);

        parser.SkipOrderBy();

        var sorts = parser.ParseSorts();

        // Anything left over means the clause was not a well formed list (eg. "Pay Name")
        if (!parser.AtEnd)
        {
            throw new WeequeryException(parser.Describe($"Unexpected '{parser.Current.Text}'", parser.Current.Position));
        }

        return sorts;
    }

    /// <summary>
    /// Step over a leading ORDER BY or OrderBy, either of which is optional.
    /// </summary>
    /// <remarks>
    /// The two spellings are not read the same way, and cannot be. ORDER BY is two words, so it is taken only
    /// when both are there, which leaves a field genuinely named Order readable: "Order DESC" is a field and a
    /// direction. OrderBy is one word with nothing after it to check, so at the front of a clause it is always
    /// the prefix. No binding may be named OrderBy for exactly that reason, so nothing legal collides with it.
    /// </remarks>
    private void SkipOrderBy()
    {
        Index += PrefixLength(Tokens, 0);
    }

    /// <summary>
    /// How many tokens the prefix takes at a given point, or zero where there is none.
    /// </summary>
    /// <remarks>
    /// Shared with <see cref="ParsedQuery"/>, which splits a combined string on the prefix and so has to
    /// recognise exactly what this skips.
    /// </remarks>
    /// <param name="tokens"></param>
    /// <param name="index">where to look</param>
    /// <returns>2 for ORDER BY, 1 for OrderBy, 0 for neither</returns>
    internal static int PrefixLength(List<QueryToken> tokens, int index)
    {
        // Two words, and only together
        if (((index + 1) < tokens.Count) && IsWord(tokens[index], "ORDER") && IsWord(tokens[index + 1], "BY")) { return 2; }

        // One word, which is the spelling a caller reaches for having written it in C#
        if ((index < tokens.Count) && IsWord(tokens[index], "ORDERBY")) { return 1; }

        return 0;
    }

    private List<Sort> ParseSorts()
    {
        List<Sort> sorts = new();

        do
        {
            sorts.Add(ParseSort());
        }
        while (Match(QueryTokenKind.Separator));

        return sorts;
    }

    /// <summary>
    /// One field, and the direction it runs if the clause says
    /// </summary>
    private Sort ParseSort()
    {
        var field = ParseField();

        // Read only here, which is what lets a field be named Asc or Desc: a direction can never start a sort
        if (Check(QueryTokenKind.Word) && DirectionLookup.TryGetValue(Current.Text, out var direction))
        {
            Index++;

            return new Sort(field, direction);
        }

        // What SQL assumes, and the same assumption a caller makes writing a field on its own
        return new Sort(field, SortDirection.Ascending);
    }

    /// <summary>
    /// A field is a bare word, a quoted one, or a bracketed one, exactly as a condition writes it. A binding key
    /// cannot hold whitespace, but it can hold punctuation the tokenizer treats as a delimiter, and a sort can
    /// name a field no binding has claimed.
    /// </summary>
    private string ParseField()
    {
        if (Match(QueryTokenKind.BracketOpen))
        {
            var bracketed = Take(QueryTokenKind.Word, "a field name");
            Take(QueryTokenKind.BracketClose, "']'");

            return bracketed.Text;
        }

        if (Check(QueryTokenKind.Text)) { return Tokens[Index++].Text; }

        return Take(QueryTokenKind.Word, "a field name").Text;
    }

    private static bool IsWord(QueryToken token, string text)
    {
        return (token.Kind == QueryTokenKind.Word) && string.Equals(token.Text, text, StringComparison.OrdinalIgnoreCase);
    }

    private bool AtEnd { get { return Index >= Tokens.Count; } }

    private QueryToken Current { get { return Tokens[Index]; } }

    private bool Check(QueryTokenKind kind)
    {
        return (!AtEnd) && (Current.Kind == kind);
    }

    private bool Match(QueryTokenKind kind)
    {
        if (!Check(kind)) { return false; }

        Index++;
        return true;
    }

    private QueryToken Take(QueryTokenKind kind, string expected)
    {
        if (!Check(kind)) { throw new WeequeryException(Describe($"Expected {expected}", PositionOfCurrentOrEnd)); }

        return Tokens[Index++];
    }

    private int PositionOfCurrentOrEnd { get { return AtEnd ? Sorts.Length : Current.Position; } }

    /// <summary>
    /// Build an error message that points at the offending part of the clause, see <see cref="QueryText"/>
    /// </summary>
    private string Describe(string message, int position)
    {
        return QueryText.Describe(Sorts, message, position);
    }
}
