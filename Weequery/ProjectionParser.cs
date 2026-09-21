namespace Weequery;

/// <summary>
/// Reads a projection. The grammar is:
/// <code>
/// projection := field (',' field)*
/// field      := (WORD | QUOTED | '[' WORD ']') ('[' (WORD | QUOTED) ']')?
/// </code>
/// A field is written the way <see cref="QueryParser"/> and <see cref="SortParser"/> write one, so
/// "Name, Pay DESC" is a sort clause, "Name, Pay" is a projection, and the field in each reads the same.
/// </summary>
/// <remarks>
/// Flat and not recursive, so there is no depth to bound: a projection is a list of names however long it runs.
/// It has no operators either, so nothing here depends on <see cref="QueryStyle"/>.
/// </remarks>
internal sealed class ProjectionParser
{
    private readonly List<QueryToken> Tokens;
    private readonly string Text;
    private int Index;

    private ProjectionParser(List<QueryToken> tokens, string text)
    {
        Tokens = tokens;
        Text = text;
    }

    /// <summary>
    /// Read a comma separated list of field names, see <see cref="Projection.Parse"/>
    /// </summary>
    /// <param name="fields">null, empty or whitespace gives <see cref="Projection.None"/></param>
    /// <returns>never null</returns>
    /// <exception cref="WeequeryException">the list is malformed</exception>
    internal static Projection Parse(string? fields)
    {
        if (string.IsNullOrWhiteSpace(fields)) { return Projection.None; }

        // Tokenized without a style, since a projection holds no operators and so has no spelling to be strict
        // about. What that decides for the tokenizer is which symbols it will refuse, and there are none here.
        var tokens = QueryTokenizer.Tokenize(fields, null);

        if (tokens.Count == 0) { return Projection.None; }

        var parser = new ProjectionParser(tokens, fields);
        var read = parser.ParseFields();

        if (!parser.AtEnd)
        {
            throw new WeequeryException(WeequeryError.QuerySyntax, parser.Describe($"Unexpected '{parser.Current.Text}'", parser.Current.Position));
        }

        return Projection.Of(read);
    }

    private List<string> ParseFields()
    {
        List<string> fields = new();

        do
        {
            fields.Add(ParseField());
        }
        while (Match(QueryTokenKind.Separator));

        return fields;
    }

    /// <summary>
    /// One field, and the index it is taken at where the brackets after it say so
    /// </summary>
    private string ParseField()
    {
        var field = ParseName();
        var index = ParseIndex(field);

        // Kept in the field's own text, a projection having nowhere else to put it, and taken apart again by
        // BindingLookup.SplitIndex when the binding is looked up
        return (index is null) ? field : $"{field}[{index}]";
    }

    /// <summary>
    /// A field is a bare word, a quoted one, or a bracketed one, exactly as a condition writes it. A binding key
    /// cannot hold whitespace, but it can hold punctuation the tokenizer treats as a delimiter.
    /// </summary>
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
    /// The index a field is read at, where the brackets after it say so: "Tallies[apples]".
    /// </summary>
    /// <remarks>
    /// Unambiguous here. A field has just been read, and what may follow it is a comma or the end, neither of
    /// which opens a bracket.
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

    private int PositionOfCurrentOrEnd { get { return AtEnd ? Text.Length : Current.Position; } }

    /// <summary>
    /// Build an error message that points at the offending part of the list, see <see cref="QueryText"/>
    /// </summary>
    private string Describe(string message, int position)
    {
        return QueryText.Describe(Text, message, position);
    }
}
