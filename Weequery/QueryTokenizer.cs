using System.Text;

namespace Weequery;

/// <summary>
/// Turns a query string into a flat token list.
/// </summary>
internal static class QueryTokenizer
{
    /// <summary>
    /// Characters that terminate an unquoted word. Note that '.', '-', ':' and '_' are deliberately absent, so
    /// property paths (A.B.C), negative numbers, dates and GUIDs need no quoting.
    /// </summary>
    private static bool IsWordTerminator(char ch)
    {
        return char.IsWhiteSpace(ch) || IsQuote(ch) || (ch is '(' or ')' or '[' or ']' or ',' or '=' or '!' or '<' or '>' or '&' or '|');
    }

    /// <summary>
    /// Both quote characters open a literal, and a literal is closed by whichever one opened it. That means either
    /// can be used freely inside the other without escaping: "it's" and 'say "hi"' both read as written.
    /// </summary>
    private static bool IsQuote(char ch)
    {
        return (ch == '\'') || (ch == '"');
    }

    /// <summary>
    /// Whether text can appear unquoted and come back as the single Word token it went in as.
    /// <para>
    /// This is the rule <see cref="QueryWriter"/> uses to decide what needs quoting, so that the writer and the
    /// tokenizer cannot drift apart. Anything containing a terminator has to be quoted, and so do the three
    /// conjunction keywords and 'null', which the tokenizer and parser give their own meaning.
    /// </para>
    /// </summary>
    /// <param name="text"></param>
    /// <returns></returns>
    public static bool IsBareWord(string? text)
    {
        if (string.IsNullOrEmpty(text)) { return false; }

        foreach (var ch in text)
        {
            if (IsWordTerminator(ch)) { return false; }
        }

        // The conjunctions and the null literal, which this reads as themselves wherever they appear, so text that
        // spells one has to be quoted to come back as text. Held with the rest of the language's words, see
        // QueryKeywords.
        return !QueryKeywords.IsKeyword(text);
    }

    public static List<QueryToken> Tokenize(string query)
    {
        List<QueryToken> tokens = new();

        if (string.IsNullOrWhiteSpace(query)) { return tokens; }

        int i = 0;
        while (i < query.Length)
        {
            char ch = query[i];

            if (char.IsWhiteSpace(ch)) { i++; continue; }

            switch (ch)
            {
                case '(':
                    tokens.Add(new(QueryTokenKind.GroupOpen, "(", i++));
                    continue;

                case ')':
                    tokens.Add(new(QueryTokenKind.GroupClose, ")", i++));
                    continue;

                case '[':
                    tokens.Add(new(QueryTokenKind.BracketOpen, "[", i++));
                    continue;

                case ']':
                    tokens.Add(new(QueryTokenKind.BracketClose, "]", i++));
                    continue;

                case ',':
                    tokens.Add(new(QueryTokenKind.Separator, ",", i++));
                    continue;

                case '\'':
                case '"':
                    i = ReadText(query, i, ch, tokens);
                    continue;
            }

            if (TryReadOperator(query, i, tokens, out int afterOperator))
            {
                i = afterOperator;
                continue;
            }

            i = ReadWord(query, i, tokens);
        }

        return tokens;
    }

    /// <summary>
    /// The characters a backslash may escape inside a quoted literal: the two quotes, and itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately just these three. A backslash in front of anything else is a backslash, and stays in the
    /// value along with what follows it, so <c>'^A\w+'</c> is the pattern it looks like rather than
    /// <c>^Aw+</c>. That matters because a value is very often a regular expression, see
    /// <see cref="Operator.IsMatch"/>, and a language that quietly ate every backslash it did not recognise
    /// would turn one into a pattern that still compiles and no longer matches.
    /// </para>
    /// <para>
    /// The escapes that are recognised are the ones there is no other way to write: a quote that would otherwise
    /// close the literal, and the backslash in front of one. Everything else a value can contain, it contains.
    /// </para>
    /// </remarks>
    private static bool IsEscapable(char ch)
    {
        return (ch == '\'') || (ch == '"') || (ch == '\\');
    }

    /// <summary>
    /// Read a quoted literal, closing on whichever quote character opened it, so both 'text' and "text" work and
    /// the other quote needs no escaping inside. A backslash escapes a quote or another backslash, so 'it\'s'
    /// yields "it's" and '\\' yields "\"; in front of anything else it is part of the value, see
    /// <see cref="IsEscapable"/>. An empty literal ('' or "") is legal and yields the empty string.
    /// </summary>
    /// <param name="query"></param>
    /// <param name="start">index of the opening quote</param>
    /// <param name="quote">the opening quote character, which is also the one that closes it</param>
    /// <param name="tokens"></param>
    /// <returns>index of the first character after the closing quote</returns>
    private static int ReadText(string query, int start, char quote, List<QueryToken> tokens)
    {
        StringBuilder builder = new();

        for (int i = (start + 1); i < query.Length; i++)
        {
            char ch = query[i];

            if ((ch == '\\') && ((i + 1) < query.Length) && IsEscapable(query[i + 1]))
            {
                builder.Append(query[i + 1]);
                i++;
                continue;
            }

            if (ch == quote)
            {
                tokens.Add(new(QueryTokenKind.Text, builder.ToString(), start));
                return i + 1;
            }

            builder.Append(ch);
        }

        throw new WeequeryException($"Unterminated {quote} quote starting at position {start}");
    }

    /// <summary>
    /// Read a comparison or conjunction operator, normalizing the alternate spellings ('=' and '&lt;&gt;') as we go.
    /// </summary>
    /// <returns>false if the character does not start an operator</returns>
    private static bool TryReadOperator(string query, int start, List<QueryToken> tokens, out int next)
    {
        next = start;

        char ch = query[start];
        char peek = ((start + 1) < query.Length) ? query[start + 1] : default;

        switch (ch)
        {
            case '=':
                // '=' and '==' both mean Equals
                tokens.Add(new(QueryTokenKind.Symbol, "==", start));
                next = (peek == '=') ? (start + 2) : (start + 1);
                return true;

            case '!':
                if (peek == '=')
                {
                    tokens.Add(new(QueryTokenKind.Symbol, "!=", start));
                    next = start + 2;
                    return true;
                }
                tokens.Add(new(QueryTokenKind.Not, "!", start));
                next = start + 1;
                return true;

            case '<':
                // '<>' is an alternate form of '!='
                tokens.Add(new(QueryTokenKind.Symbol, (peek == '>') ? "!=" : (peek == '=') ? "<=" : "<", start));
                next = ((peek == '>') || (peek == '=')) ? (start + 2) : (start + 1);
                return true;

            case '>':
                tokens.Add(new(QueryTokenKind.Symbol, (peek == '=') ? ">=" : ">", start));
                next = (peek == '=') ? (start + 2) : (start + 1);
                return true;

            case '&':
                if (peek != '&') { throw new WeequeryException($"Expected '&&' at position {start}"); }
                tokens.Add(new(QueryTokenKind.And, "&&", start));
                next = start + 2;
                return true;

            case '|':
                if (peek != '|') { throw new WeequeryException($"Expected '||' at position {start}"); }
                tokens.Add(new(QueryTokenKind.Or, "||", start));
                next = start + 2;
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Read an unquoted word, promoting the conjunction keywords to their symbolic equivalents.
    /// Everything else stays a Word; whether it is a field, an operator name or a literal is decided by position.
    /// </summary>
    /// <returns>index of the first character after the word</returns>
    private static int ReadWord(string query, int start, List<QueryToken> tokens)
    {
        int end = start;
        while ((end < query.Length) && (!IsWordTerminator(query[end]))) { end++; }

        string word = query[start..end];

        switch (word.ToUpperInvariant())
        {
            case "AND":
                tokens.Add(new(QueryTokenKind.And, "&&", start));
                break;

            case "OR":
                tokens.Add(new(QueryTokenKind.Or, "||", start));
                break;

            case "NOT":
                tokens.Add(new(QueryTokenKind.Not, "!", start));
                break;

            default:
                tokens.Add(new(QueryTokenKind.Word, word, start));
                break;
        }

        return end;
    }
}
