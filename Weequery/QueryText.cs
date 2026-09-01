namespace Weequery;

/// <summary>
/// How the parsers quote the text they were given when they refuse it, so that a condition and a sort clause
/// report a problem the same way.
/// </summary>
internal static class QueryText
{
    /// <summary>
    /// How much of the text an error message quotes. Enough to see what the message is pointing at, and no more:
    /// the text is caller input, so a malformed one can be any length, and quoting all of it puts that length in
    /// an exception message and from there into a log.
    /// </summary>
    private const int MaxQuoted = 120;

    /// <summary>
    /// The part of the text worth showing: all of it when it is short, otherwise a window around the position the
    /// message names, marked with ellipses so it reads as the excerpt it is. The position is always into the whole
    /// text, not into the excerpt.
    /// </summary>
    /// <param name="text"></param>
    /// <param name="position">where the message is pointing</param>
    /// <returns></returns>
    internal static string Excerpt(string text, int position)
    {
        if (text.Length <= MaxQuoted) { return text; }

        var start = Math.Clamp(position - (MaxQuoted / 2), 0, text.Length - MaxQuoted);
        var lead = (start > 0) ? "..." : string.Empty;
        var trail = ((start + MaxQuoted) < text.Length) ? "..." : string.Empty;

        return $"{lead}{text.Substring(start, MaxQuoted)}{trail}";
    }

    /// <summary>
    /// An error message that points at the offending part of the text
    /// </summary>
    /// <param name="text"></param>
    /// <param name="message"></param>
    /// <param name="position"></param>
    /// <returns></returns>
    internal static string Describe(string text, string message, int position)
    {
        return (position >= text.Length)
            ? $"{message} but the query ended: '{Excerpt(text, position)}'"
            : $"{message} at position {position}: '{Excerpt(text, position)}'";
    }
}
