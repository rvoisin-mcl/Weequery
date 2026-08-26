namespace Weequery;

/// <summary>
/// The words the query language gives its own meaning, which is what a binding key may not be.
/// </summary>
/// <remarks>
/// <para>
/// A key is written as a bare field name, so a key that spells an operator makes a query that reads two ways.
/// Some of those the parser settles by position, since a field comes before an operator, but the conjunctions do
/// not survive that far: the tokenizer promotes AND, OR and NOT wherever they appear, so a field named "And"
/// cannot be written at all, bracketed or not. Rather than have some collisions work and others fail, none are
/// allowed, and they are refused when the binding is made rather than when a query using it will not parse.
/// </para>
/// <para>
/// The operator names are read from <see cref="Operator"/> rather than listed, so an operator added later is
/// reserved by having been added. The symbolic spellings need no entry: '==' and '&gt;' are not valid names, so a
/// key could never be one.
/// </para>
/// </remarks>
internal static class QueryKeywords
{
    /// <summary>
    /// The words the tokenizer gives a meaning of their own, wherever they appear. A value or a field name that
    /// spells one of these has to be quoted to be read as itself, which is what
    /// <see cref="QueryTokenizer.IsBareWord"/> is asking about.
    /// </summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "AND",
        "OR",
        "NOT",
        "NULL",
    };

    /// <summary>
    /// Everything above, plus the words the parser reads as an operator where one is expected: the words SQL
    /// spells its operators with, and the name of every <see cref="Operator"/>.
    /// </summary>
    private static readonly HashSet<string> Reserved = BuildReserved();

    /// <summary>
    /// Whether the tokenizer gives this word a meaning of its own
    /// </summary>
    /// <param name="text"></param>
    /// <returns></returns>
    internal static bool IsKeyword(string text)
    {
        return Keywords.Contains(text);
    }

    /// <summary>
    /// Whether the query language claims this word, so whether it is unusable as a binding key
    /// </summary>
    /// <param name="text"></param>
    /// <returns></returns>
    internal static bool IsReserved(string? text)
    {
        return (text is not null) && Reserved.Contains(text);
    }

    /// <summary>
    /// The reserved set, gathered rather than listed
    /// </summary>
    /// <returns></returns>
    private static HashSet<string> BuildReserved()
    {
        HashSet<string> reserved = new(Keywords, StringComparer.OrdinalIgnoreCase)
        {
            // The multi-word SQL spellings, word by word, since that is how the parser reads them: IS NULL,
            // IS NOT NULL, NOT IN, NOT BETWEEN
            "IS",
            "IN",
            "BETWEEN",
        };

        // And the canonical name of every operator. The ones spelled with symbols are skipped by the same rule
        // that decides a key: they are not names.
        foreach (var op in Enum.GetValues<Operator>())
        {
            var spelling = ConditionFunctions.GetOperationString(op);

            if (WeequeryException.IsSqlName(spelling)) { reserved.Add(spelling); }
        }

        return reserved;
    }
}
