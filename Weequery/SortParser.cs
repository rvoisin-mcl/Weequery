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
/// field written without a direction sorts ascending, as it does in SQL. Under
/// <see cref="QueryStyle.Native"/> the two word prefix is refused and only OrderBy is read, see
/// <see cref="PrefixLength"/>.
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
    /// <param name="style">
    /// <see cref="QueryStyle.Native"/> to take only the one word prefix, see <see cref="PrefixLength"/>. Null, or
    /// either deprecated style, takes both spellings
    /// </param>
    /// <returns>never null; empty when there was nothing to read and no default</returns>
    /// <exception cref="WeequeryException">the clause is malformed, or spells the prefix a way the style refuses</exception>
    public static List<Sort> Parse(string? sortString, IEnumerable<Sort>? defaultSort, QueryStyle style = QueryStyle.Native)
    {
        var tokens = QueryTokenizer.Tokenize(sortString ?? string.Empty, style);

        // Nothing asked for, return the default
        if (tokens.Count == 0) { return [.. defaultSort ?? []]; }

        var sorts = ParseLeading(tokens, sortString!, defaultSort, out var stopped, style);

        // Anything left over means the clause was not a well formed list (eg. "Pay Name")
        if (stopped < tokens.Count)
        {
            throw new WeequeryException(WeequeryError.QuerySyntax, QueryText.Describe(sortString!, $"Unexpected '{tokens[stopped].Text}'", tokens[stopped].Position));
        }

        return sorts;
    }

    /// <summary>
    /// Read as much of a sort clause as there is, and say where it stopped rather than refusing what follows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same split <see cref="QueryParser.ParseLeading"/> performs, for the same reason: a combined string
    /// puts a projection after the sorts, so something has to read the sorts and hand back where they ended
    /// instead of treating the next word as a malformed field. See <see cref="ParsedQuery"/>.
    /// </para>
    /// <para>
    /// Where the clause ends is found by reading it, not by searching the text, so a field genuinely spelled
    /// like what follows is still a field.
    /// </para>
    /// </remarks>
    /// <param name="tokens">the clause, already tokenized</param>
    /// <param name="sorts">the text those tokens came from, for the error messages</param>
    /// <param name="defaultSort">what to sort by where there was nothing to read, copied rather than kept</param>
    /// <param name="stopped">the token the clause stopped at, or the count where it ran to the end</param>
    /// <param name="style">
    /// <see cref="QueryStyle.Native"/> to take only the one word prefix, see <see cref="PrefixLength"/>
    /// </param>
    /// <returns>never null</returns>
    /// <exception cref="WeequeryException">the clause is malformed, or spells the prefix a way the style refuses</exception>
    internal static List<Sort> ParseLeading(List<QueryToken> tokens, string sorts, IEnumerable<Sort>? defaultSort, out int stopped, QueryStyle style = QueryStyle.Native)
    {
        WeequeryException.ThrowIfNull(tokens);

        if (tokens.Count == 0)
        {
            stopped = 0;

            return [.. defaultSort ?? []];
        }

        var parser = new SortParser(tokens, sorts);

        parser.SkipOrderBy(style);

        var read = parser.ParseSorts();

        stopped = parser.Index;

        return read;
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
    private void SkipOrderBy(QueryStyle style = QueryStyle.Native)
    {
        Index += PrefixLength(Tokens, 0, style);
    }

    /// <summary>
    /// How many tokens the prefix takes at a given point, or zero where there is none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shared with <see cref="ParsedQuery"/>, which splits a combined string on the prefix and so has to
    /// recognise exactly what this skips.
    /// </para>
    /// <para>
    /// <see cref="QueryStyle.Native"/> takes only OrderBy. The rule it applies to the operators, that a name is
    /// one word, is not one a separator gets to be exempt from for being a separator. The two word spelling is
    /// still recognised here rather than simply going unmatched, so what a caller gets back names the spelling to
    /// use instead of reporting a stray word called ORDER.
    /// </para>
    /// </remarks>
    /// <param name="tokens"></param>
    /// <param name="index">where to look</param>
    /// <param name="style">
    /// <see cref="QueryStyle.Native"/> to refuse ORDER BY, null or either deprecated style to take it
    /// </param>
    /// <returns>2 for ORDER BY, 1 for OrderBy, 0 for neither</returns>
    /// <exception cref="WeequeryException">the style refuses the spelling that is there</exception>
    internal static int PrefixLength(List<QueryToken> tokens, int index, QueryStyle style = QueryStyle.Native)
    {
        // Two words, and only together
        if (((index + 1) < tokens.Count) && IsWord(tokens[index], "ORDER") && IsWord(tokens[index + 1], "BY"))
        {
            if (style == QueryStyle.Native)
            {
                throw new WeequeryException(WeequeryError.QuerySyntax, $"'ORDER BY' at position {tokens[index].Position} is not valid in the {nameof(QueryStyle.Native)} style, write 'OrderBy'");
            }

            return 2;
        }

        // One word, which is the spelling a caller reaches for having written it in C#, and the only one Native
        // will take
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
        var field = ParseName();
        var index = ParseIndex(field);

        // Kept in the field's own text, a Sort having nowhere else to put it, and taken apart again by
        // BindingLookup.SplitIndex when the binding is looked up
        return (index is null) ? field : $"{field}[{index}]";
    }

    private string ParseName()
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

    /// <summary>
    /// The index a sort field is taken at, where the brackets after it say so: "Tallies[apples] DESC".
    /// </summary>
    /// <remarks>
    /// Unambiguous here. A field has just been read, and what may follow it is a direction, a comma or the end,
    /// none of which opens a bracket.
    /// </remarks>
    /// <param name="field"></param>
    /// <returns>the index as text, or null where the field carries none</returns>
    private string? ParseIndex(string field)
    {
        if (!Check(QueryTokenKind.BracketOpen)) { return null; }

        var open = Current.Position;
        Index++;

        if (!(Check(QueryTokenKind.Word) || Check(QueryTokenKind.Text)))
        {
            throw new WeequeryException(WeequeryError.QuerySyntax, Describe($"Expected an index for field '{field}'", open));
        }

        var index = Tokens[Index++].Text;

        Take(QueryTokenKind.BracketClose, "']'");

        return index;
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
        if (!Check(kind)) { throw new WeequeryException(WeequeryError.QuerySyntax, Describe($"Expected {expected}", PositionOfCurrentOrEnd)); }

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
