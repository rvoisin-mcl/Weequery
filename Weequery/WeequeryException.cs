using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

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
    /// ctor
    /// </summary>
    /// <param name="message">names the offending input</param>
    public WeequeryException(string message) : base(message)
    { }

    /// <summary>
    /// ctor
    /// </summary>
    /// <param name="message">names the offending input</param>
    /// <param name="inner">what was caught, where the failure came from further down</param>
    public WeequeryException(string message, Exception inner) : base(message, inner)
    { }

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
            throw new WeequeryException($"{paramName} cannot be null");
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
            throw new WeequeryException($"{paramName} cannot be null or empty");
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
            throw new WeequeryException($"{paramName} cannot be empty if provided");
        }
    }

    /// <summary>
    /// Require the argument to be a valid unquoted SQL name: an ASCII letter or an underscore, followed by any
    /// number of ASCII letters, digits or underscores.
    /// <para>
    /// Used for binding keys. A key is written as a bare field name in the query language and lines up with a
    /// column name in the database, so anything a database would need quoting for is rejected here. That rules out
    /// whitespace, a leading digit, and every punctuation character except the underscore, periods, hyphens and
    /// slashes included.
    /// </para>
    /// <para>
    /// Deliberately ASCII only. Several databases do accept Unicode letters in an unquoted name, but they disagree
    /// on which, so the portable subset is the safer contract for a key that has to work everywhere.
    /// </para>
    /// </summary>
    /// <param name="argument"></param>
    /// <param name="paramName"></param>
    /// <exception cref="WeequeryException"></exception>
    public static void ThrowIfNotSqlName(string? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
        if (argument is null) { return; }

        if (!IsSqlName(argument))
        {
            throw new WeequeryException($"{paramName} must be a valid unquoted SQL name, a letter or underscore followed by letters, digits or underscores, so '{argument}' is not allowed");
        }
    }

    /// <summary>
    /// Require the argument to be usable as a binding key: a valid SQL name, as above, that the query language has
    /// not already claimed for itself.
    /// <para>
    /// A key is written as a bare field name, so one that spells an operator makes a query that reads two ways.
    /// The parser settles some of those by position, but not the conjunctions: the tokenizer promotes AND, OR and
    /// NOT wherever they appear, so a field named "And" cannot be written at all. Refused here rather than left to
    /// fail as a query that will not parse, and refused for all of them rather than only the ones that break, so
    /// there is one rule to remember. See <see cref="QueryKeywords"/>.
    /// </para>
    /// </summary>
    /// <param name="argument"></param>
    /// <param name="paramName"></param>
    /// <exception cref="WeequeryException"></exception>
    public static void ThrowIfNotBindingKey(string? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
        if (argument is null) { return; }

        ThrowIfNotSqlName(argument, paramName);

        if (QueryKeywords.IsReserved(argument))
        {
            throw new WeequeryException($"{paramName} '{argument}' is a word the query language reads as an operator, so a query could not tell it from one; bind it under a different key");
        }
    }

    /// <summary>
    /// Whether the text is a valid unquoted SQL name, as described on <see cref="ThrowIfNotSqlName"/>
    /// </summary>
    /// <param name="text"></param>
    /// <returns></returns>
    public static bool IsSqlName(string? text)
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
