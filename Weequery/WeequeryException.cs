using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Weequery.Parsing;

namespace Weequery;

/// <summary>
/// Everything Weequery refuses is refused with this: a query that will not parse, a field no binding claimed, an
/// operator that does not apply to the property it names, a value that will not convert, a condition nested past
/// the limit. The message names the offending input, so it is worth reading before the stack trace.
/// </summary>
/// <remarks>
/// Much of what it reports is the caller's input rather than the programmer's mistake a filter arriving from a
/// client is the usual source so a request handler will normally catch it and answer with a bad request rather
/// than let it become an error.
/// </remarks>
public class WeequeryException : Exception
{
    /// <summary>
    /// Why this was thrown, as something to branch on rather than read. See <see cref="WeequeryError"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="Exception.HResult"/> carries the same thing for anything that only sees the base type, and the
    /// two cannot disagree: both are set from the value handed to the constructor.
    /// </remarks>
    public WeequeryError Error { get; }

    /// <summary>
    /// The facility and the customer bit, which is what tells an HResult of ours from everybody else's.
    /// </summary>
    /// <remarks>
    /// Bit 31 for failure and bit 29 for customer defined, which is the half of the layout that is not ours to
    /// choose. The facility is, and 4 is as good as any: nothing reads it, it only has to stay put.
    /// </remarks>
    private const uint Facility = 0xA0040000;

    /// <summary>
    /// The <see cref="Exception.HResult"/> a reason is reported as.
    /// </summary>
    /// <remarks>
    /// Public because a caller matching on the number needs to be able to write it down without copying a
    /// literal out of a debugger, and because a caller reading an exception across a boundary that has lost the
    /// type has nothing else to compare against.
    /// </remarks>
    /// <param name="error"></param>
    /// <returns></returns>
    public static int HResultFor(WeequeryError error)
    {
        return unchecked((int)(Facility | (uint)error));
    }

    /// <summary>
    /// ctor
    /// </summary>
    /// <remarks>
    /// Reports <see cref="WeequeryError.Unspecified"/>. Kept for the callers who were constructing one of these
    /// before there was a reason to give; prefer the overload that takes one.
    /// </remarks>
    /// <param name="message">names the offending input</param>
    public WeequeryException(string message) : this(WeequeryError.Unspecified, message)
    { }

    /// <summary>
    /// ctor
    /// </summary>
    /// <inheritdoc cref="WeequeryException(string)" path="/remarks"/>
    /// <param name="message">names the offending input</param>
    /// <param name="inner">what was caught, where the failure came from further down</param>
    public WeequeryException(string message, Exception inner) : this(WeequeryError.Unspecified, message, inner)
    { }

    /// <summary>
    /// ctor
    /// </summary>
    /// <param name="error">why, see <see cref="WeequeryError"/></param>
    /// <param name="message">names the offending input</param>
    public WeequeryException(WeequeryError error, string message) : base(message)
    {
        Error = error;
        HResult = HResultFor(error);
    }

    /// <summary>
    /// ctor
    /// </summary>
    /// <param name="error">why, see <see cref="WeequeryError"/></param>
    /// <param name="message">names the offending input</param>
    /// <param name="inner">what was caught, where the failure came from further down</param>
    public WeequeryException(WeequeryError error, string message, Exception inner) : base(message, inner)
    {
        Error = error;
        HResult = HResultFor(error);
    }

    /// <summary>
    /// Throw if the argument is null, naming it as the caller wrote it
    /// </summary>
    /// <param name="argument"></param>
    /// <param name="paramName">filled in by the compiler</param>
    /// <exception cref="WeequeryException"></exception>
    public static void ThrowIfNull([NotNull] object? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
        if (argument is null)
        {
            throw new WeequeryException(WeequeryError.ArgumentMissing, $"{paramName} cannot be null");
        }
    }

    /// <summary>
    /// Throw if the argument is null or the empty string, naming it as the caller wrote it
    /// </summary>
    /// <param name="argument"></param>
    /// <param name="paramName">filled in by the compiler</param>
    /// <exception cref="WeequeryException"></exception>
    public static void ThrowIfNullOrEmpty([NotNull] string? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
        if (string.IsNullOrEmpty(argument))
        {
            throw new WeequeryException(WeequeryError.ArgumentMissing, $"{paramName} cannot be null or empty");
        }
    }

    /// <summary>
    /// Throw if the argument is null or holds nothing, naming it as the caller wrote it. The same words the
    /// string overload uses, so a path written as segments and the same path written as a string report alike.
    /// </summary>
    /// <param name="argument"></param>
    /// <param name="paramName">filled in by the compiler</param>
    /// <exception cref="WeequeryException"></exception>
    public static void ThrowIfNullOrEmpty([NotNull] string[]? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
        if ((argument is null) || (argument.Length == 0))
        {
            throw new WeequeryException(WeequeryError.ArgumentMissing, $"{paramName} cannot be null or empty");
        }
    }

    /// <summary>
    /// Throw if the argument was provided but is the empty string. For the optional ones, where leaving it out is
    /// meaningful and passing nothing is not.
    /// </summary>
    /// <param name="argument"></param>
    /// <param name="paramName">filled in by the compiler</param>
    /// <exception cref="WeequeryException"></exception>
    public static void ThrowIfNotNullButEmpty(string? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
        if ((argument is not null) && (argument.Length == 0))
        {
            throw new WeequeryException(WeequeryError.ArgumentMissing, $"{paramName} cannot be empty if provided");
        }
    }

    /// <summary>
    /// Require the argument to be a valid unquoted SQL name: an ASCII letter or an underscore, followed by any
    /// number of ASCII letters, digits or underscores.
    /// <para>
    /// One name, so no period: this is what a single column may be called. A binding key is a period separated
    /// sequence of these, see <see cref="ThrowIfNotBindingKey"/>, which is the rule keys are actually held to.
    /// Anything a database would need quoting for is rejected either way, which rules out whitespace, a leading
    /// digit, and every punctuation character except the underscore, hyphens and slashes included.
    /// </para>
    /// <para>
    /// Deliberately ASCII only. Several databases do accept Unicode letters in an unquoted name, but they disagree
    /// on which, so the portable subset is the safer contract for a key that has to work everywhere.
    /// </para>
    /// </summary>
    /// <param name="argument"></param>
    /// <param name="paramName"></param>
    /// <exception cref="WeequeryException"></exception>
    public static void ThrowIfNotKeyName(string? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
        if (argument is null) { return; }

        if (!IsKeyName(argument))
        {
            throw new WeequeryException(WeequeryError.KeyInvalid, $"{paramName} will not accept '{argument}', which must be a valid unquoted SQL name, a letter or underscore followed by letters, digits or underscores");
        }
    }

    /// <summary>
    /// Require the argument to be usable as a binding key: one or more valid SQL names separated by periods, that
    /// the query language has not already claimed for itself.
    /// <para>
    /// The period is there so a nested property can be bound under the name it already has.
    /// <c>BindProperty("Lair.Capacity")</c> needed an explicit key before, since the path it would otherwise be
    /// keyed by was not a legal one; now the path is a legal key and the call stands on its own. A period is not
    /// a delimiter in the query language, so <c>Lair.Capacity</c> is one word to the tokenizer and reads back
    /// unquoted like any other key.
    /// </para>
    /// <para>
    /// Every segment is held to the whole of <see cref="ThrowIfNotKeyName"/>, so a leading or trailing period, two
    /// in a row, and a segment starting with a digit are all still refused. What is legal is a name; what is now
    /// also legal is several of them joined.
    /// </para>
    /// <para>
    /// A key is written as a bare field name, so one that spells an operator makes a query that reads two ways.
    /// The parser settles some of those by position, but not the conjunctions: the tokenizer promotes AND, OR and
    /// NOT wherever they appear, so a field named "And" cannot be written at all. Refused here rather than left to
    /// fail as a query that will not parse, and refused for all of them rather than only the ones that break, so
    /// there is one rule to remember. Only the whole key is checked, since only a whole word is promoted:
    /// "Lair.And" is one word to the tokenizer and is not the conjunction. See <see cref="QueryKeywords"/>.
    /// </para>
    /// </summary>
    /// <param name="argument"></param>
    /// <param name="paramName"></param>
    /// <exception cref="WeequeryException"></exception>
    public static void ThrowIfNotBindingKey(string? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
        if (argument is null) { return; }

        // Said properly rather than left to the general rule below, which would report the brackets as stray
        // punctuation. A key holding them could not be told from a field with an index after it: "Labels[0]" as a
        // key and "Labels[0]" as element zero of Labels are the same text and different questions, and the parser
        // has to pick one. So an element of a collection is given a name, which is one the caller sees anyway.
        if (argument.Contains('['))
        {
            throw new WeequeryException(WeequeryError.KeyInvalid, $"'{argument}' cannot be a key, brackets indicate a single element of a collection");
        }

        if (!IsQualifiedKeyName(argument))
        {
            throw new WeequeryException(WeequeryError.KeyInvalid, $"{paramName} cannot accept '{argument}', which must be one or more valid unquoted names separated by periods");
        }

        if (QueryKeywords.IsReserved(argument))
        {
            throw new WeequeryException(WeequeryError.KeyInvalid, $"{paramName} '{argument}' is a reserved keyword for the query language; bind it under a different key");
        }
    }

    /// <summary>
    /// If the text is shaped like a binding key: one or more <see cref="IsKeyName"/> segments separated by
    /// periods. Says nothing about if the language has already claimed it, which
    /// <see cref="IsBindingKey"/> does as well.
    /// </summary>
    /// <param name="text"></param>
    /// <returns></returns>
    public static bool IsQualifiedKeyName(string? text)
    {
        if (string.IsNullOrEmpty(text)) { return false; }

        // Split refuses a leading or trailing period and two in a row on its own, since each produces an empty
        // segment and an empty segment is not a name
        foreach (var segment in text.Split('.'))
        {
            if (!IsKeyName(segment)) { return false; }
        }

        return true;
    }

    /// <summary>
    /// If the text can be used as a binding key, which is everything
    /// <see cref="ThrowIfNotBindingKey"/> requires.
    /// </summary>
    /// <param name="text"></param>
    /// <returns></returns>
    public static bool IsBindingKey(string? text)
    {
        return IsQualifiedKeyName(text) && (!QueryKeywords.IsReserved(text));
    }

    /// <summary>
    /// If the text is a valid unquoted SQL name, as described on <see cref="ThrowIfNotKeyName"/>. One name,
    /// so no period; <see cref="IsBindingKey"/> is the rule a key is held to.
    /// </summary>
    /// <param name="text"></param>
    /// <returns></returns>
    public static bool IsKeyName(string? text)
    {
        if (string.IsNullOrEmpty(text)) { return false; }

        var first = text[0];
        if (!(((first >= 'a') && (first <= 'z')) || ((first >= 'A') && (first <= 'Z')) || (first == '_'))) { return false; }

        foreach (var ch in text)
        {
            var allowed = ((ch >= 'a') && (ch <= 'z')) || ((ch >= 'A') && (ch <= 'Z')) || ((ch >= '0') && (ch <= '9')) || (ch == '_');
            if (!allowed) { return false; }
        }

        return true;
    }
}
