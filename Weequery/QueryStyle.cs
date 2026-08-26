namespace Weequery;

/// <summary>
/// Which spelling to use when writing a condition out as a query string.
/// <para>
/// The parser accepts both, so this only affects what is produced, never what can be read. Both styles round trip,
/// and a condition written in either selects the same rows.
/// </para>
/// <para>
/// Only the operators that genuinely have two spellings are affected. The named operators (IsNull, IsIn,
/// IsBetween, StartsWith and the rest) are written the same way whichever style is chosen. The parser does read
/// the SQL spellings of some of them, so IN, NOT IN, IS NULL, IS NOT NULL, BETWEEN and NOT BETWEEN all work as
/// input, but writing settles on the one canonical name per operator so there is a single form to compare.
/// </para>
/// </summary>
public enum QueryStyle
{
    /// <summary>
    /// C# spelling: <c>&amp;&amp;</c>, <c>||</c>, <c>!</c>, <c>==</c>, <c>!=</c>. The default, and what
    /// <see cref="object.ToString"/> produces.
    /// </summary>
    CSharp,

    /// <summary>
    /// SQL spelling: <c>AND</c>, <c>OR</c>, <c>NOT</c>, <c>=</c>, <c>&lt;&gt;</c>
    /// </summary>
    Sql,
}
