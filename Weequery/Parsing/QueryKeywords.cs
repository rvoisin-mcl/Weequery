namespace Weequery.Parsing;

/// <summary>
/// The words reserved for the sole use of the query language, cannot be used as a binding key
/// </summary>
/// <remarks>
/// <para>
/// The operator names are read from <see cref="Operator"/> rather than listed, so an operator added later is
/// reserved by having been added. The symbolic spellings need no entry: '==' and '&gt;' are not valid binding names,
/// and so do not need to be tested
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
    /// If the tokenizer gives this word a meaning of its own
    /// </summary>
    /// <param name="text"></param>
    /// <returns></returns>
    internal static bool IsKeyword(string text)
    {
        return Keywords.Contains(text);
    }

    /// <summary>
    /// If the query language claims this word, so if it is unusable as a binding key
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
            // What a query string will be split on, see ParsedQuery
            "OrderBy",
            "Select",
        };

        // And the canonical name of every operator. The symbolic ones are skipped as they are invalid as binding names
        foreach (var op in Enum.GetValues<Operator>())
        {
            var spelling = ConditionFunctions.GetOperationString(op);

            if (WeequeryException.IsKeyName(spelling)) { reserved.Add(spelling); }
        }

        return reserved;
    }
}
