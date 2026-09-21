namespace Weequery;

/// <summary>
/// Which spelling of the query language to use.
/// <para>
/// Writing a condition out, this picks the spelling for the operators that have more than one, see
/// <see cref="ConditionFunctions.ToQuery"/>. Reading one in, it picks how strict the parser is about the
/// spellings it will accept, see <see cref="ConditionFunctions.ParseQuery"/>. <see cref="Native"/> is the only
/// value that does the second thing; it is the one style with exactly one spelling per operator, so it is the
/// only one there is anything to be strict about.
/// </para>
/// </summary>
public enum QueryStyle
{
    /// <summary>
    /// C# spelling: <c>&amp;&amp;</c>, <c>||</c>, <c>!</c>, <c>==</c>, <c>!=</c>.
    /// </summary>
    [Obsolete("The C# and SQL styles are deprecated in favour of " + nameof(Native) + ", which spells every operator exactly one way. This still writes what it always wrote, and the parser still reads it.")]
    CSharp,

    /// <summary>
    /// SQL spelling: <c>AND</c>, <c>OR</c>, <c>NOT</c>, <c>=</c>, <c>&lt;&gt;</c>
    /// </summary>
    [Obsolete("The C# and SQL styles are deprecated in favour of " + nameof(Native) + ", which spells every operator exactly one way. This still writes what it always wrote, and the parser still reads it.")]
    Sql,

    /// <summary>
    /// Weequery's own spelling, and the one to use. One form per operator and no alternates:
    /// <c>AND</c>, <c>OR</c>, <c>NOT</c>, <c>=</c>, <c>&lt;&gt;</c>, and every named operator written as its own
    /// name, in one word.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The other two styles exist because the language grew up accepting whatever a caller was likely to type,
    /// which left most operators with two or three spellings and left a written condition looking like whichever
    /// language its author had in mind. Native is the settlement: one spelling, so two conditions that mean the
    /// same thing are written the same way and can be compared as text.
    /// </para>
    /// <para>
    /// <b>Writing</b>, it produces:
    /// <code>
    /// (([Pay] &gt; '10000') AND ([IsActive] = 'true'))
    /// NOT ([Alias] IsNull)
    /// ([Pay] &lt;&gt; '12000')
    /// ([Pay] IsBetween ('8000', '12000'))
    /// </code>
    /// </para>
    /// <para>
    /// <b>Reading</b>, it is the strict grammar. What it refuses is every spelling of an operator that runs to
    /// more than one word, and the three conjunction symbols. Each refusal names the spelling to use instead.
    /// <list type="table">
    /// <item><term><c>&amp;&amp;</c>, <c>||</c>, <c>!</c></term><description>refused; write AND, OR, NOT</description></item>
    /// <item><term><c>IS NULL</c>, <c>IS NOT NULL</c></term><description>refused; write IsNull, IsNotNull</description></item>
    /// <item><term><c>NOT IN</c>, <c>NOT BETWEEN</c></term><description>refused; write IsNotIn, IsNotBetween</description></item>
    /// <item><term><c>IsBetween 1 AND 5</c></term><description>refused; write IsBetween (1, 5)</description></item>
    /// <item><term><c>ORDER BY</c></term><description>refused; write OrderBy, see <see cref="ParsedQuery"/></description></item>
    /// </list>
    /// </para>
    /// <para>
    /// The one word alternates stay legal, since a name that is already one word breaks no rule by being an
    /// alternate: <c>IN</c> and <c>BETWEEN</c> read as IsIn and IsBetween, and <c>==</c> and <c>!=</c> read as
    /// the two comparisons. Writing still settles on one form for each, so a condition read from any of them
    /// comes back out as <c>IsIn</c>, <c>IsBetween</c>, <c>=</c> and <c>&lt;&gt;</c>.
    /// </para>
    /// <para>
    /// Operator names are matched without regard to case, as field names and keywords are, so <c>isnull</c> and
    /// <c>IsNull</c> are the same word. What Native removes is spellings that are separate words, not alternate
    /// casing.
    /// </para>
    /// </remarks>
    Native,
}
