namespace Weequery;

/// <summary>
/// The operation a condition performs.
/// </summary>
/// <remarks>
/// <para>
/// Note on nulls. A null satisfies nothing except <see cref="IsNull"/>. Every other operator is built as "the
/// property has a value" ANDed with the test on that value, so a null is not caught by the negative operators
/// either: it is not "not equal to 5", it is unknown, exactly as a database treats it. So for any column,
/// the rows matching an operator, the rows matching its negation, and the rows that are null partition the table
/// between them, and one condition gives the same answer whether it runs against a database or in memory.
/// <see cref="Not"/> is the exception, because it negates the whole test rather than the value test: the guard is
/// inside what it negates, so the null rows come back. That makes "!(Alias == 'Ghost')" and "Alias != 'Ghost'"
/// two different questions where the column is nullable, the first including the rows with no alias and the
/// second not. Both are useful, so neither is normalised into the other, but they are not interchangeable.
/// </para>
/// <para>
/// This extends to a property reached through a nullable. A path may step into a Nullable&lt;T&gt;, so
/// "BirthDate.Year" on a DateTime? is legal, and the result behaves as a nullable in its own right even though
/// Year is an int: "BirthDate.Year IsNull" is true exactly when BirthDate is null, and a comparison on it simply
/// does not match a row whose BirthDate is null. A path through a null *reference* is the same story: "Lair.Name"
/// where the minion has no lair matches nothing, and "Lair.Name IsNull" asks whether the lair is there. A database
/// answers that through the join, and guarding it here is what makes the two give the same answer.
/// </para>
/// <para>
/// Note on string matching. The six substring operators (StartsWith, DoesNotStartWith, EndsWith, DoesNotEndWith,
/// Contains, DoesNotContain) are built from the framework's own string methods, so the rules that decide what
/// counts as a match come from wherever the query is finally evaluated, not from Weequery:
/// </para>
/// <list type="bullet">
/// <item><description>
/// In memory (LINQ to Objects, which includes anything built by Weequery&lt;T&gt;.BuildDelegate, or applied to an
/// IQueryable over an in-memory collection): StartsWith and EndsWith use the framework's culture sensitive
/// linguistic comparison, against CultureInfo.CurrentCulture, while Contains is ordinal. The three therefore do
/// not agree with each other.
/// </description></item>
/// <item><description>
/// Against a database through EF Core: each operator is translated to SQL (LIKE, instr, strpos and so on) and the
/// collation of the column decides the result, including whether the match is case sensitive.
/// </description></item>
/// </list>
/// The practical consequence is that one condition can match different rows depending on where it runs. For
/// example, against a value whose first character is a soft hyphen (U+00AD) followed by "Acme", StartsWith 'Acme' matches in memory,
/// because linguistic comparison treats a soft hyphen as ignorable, but does not match on SQLite, because LIKE
/// compares the stored characters. Case sensitivity varies by provider as well: LIKE is case insensitive for
/// ASCII on SQLite but case sensitive on PostgreSQL.
/// </remarks>
public enum Operator
{
    /// <summary>Bound property is null. Requires a nullable property and takes no value</summary>
    IsNull,

    /// <summary>Bound property is not null. Requires a nullable property and takes no value</summary>
    IsNotNull,

    /// <summary>Bound property equals the value</summary>
    Equals,

    /// <summary>
    /// Bound property has a value and it is not the value given. A null does not match: it is unknown, not
    /// "not equal to", which is what a database answers. See the remarks on <see cref="Operator"/>
    /// </summary>
    NotEqual,

    /// <summary>Bound property is less than the value</summary>
    LessThan,

    /// <summary>Bound property is less than or equal to the value</summary>
    LessThanOrEqual,

    /// <summary>Bound property is greater than the value</summary>
    GreaterThan,

    /// <summary>Bound property is greater than or equal to the value</summary>
    GreaterThanOrEqual,

    /// <summary>Bound property is within the two values, inclusive of both</summary>
    IsBetween,

    /// <summary>
    /// Bound property has a value and it falls outside the two. A null does not match, see the remarks on
    /// <see cref="Operator"/>
    /// </summary>
    IsNotBetween,

    /// <summary>
    /// Bound property equals one of the values. With no values, matches nothing, since there is nothing to be
    /// one of
    /// </summary>
    IsIn,

    /// <summary>
    /// Bound property has a value and it equals none of the values. A null does not match, see the remarks on
    /// <see cref="Operator"/>. With no values, nothing is excluded, so every row that has a value matches
    /// </summary>
    IsNotIn,

    /// <summary>
    /// Bound property begins with the value. See the remarks on <see cref="Operator"/>: in memory this is a
    /// culture sensitive comparison, against a database it follows the column's collation
    /// </summary>
    StartsWith,

    /// <summary>
    /// Bound property has a value and it does not begin with the value given. A null does not match, no more than
    /// it does for StartsWith. See the remarks on <see cref="Operator"/> for that, and for how the comparison
    /// rules differ between in-memory and database evaluation
    /// </summary>
    DoesNotStartWith,

    /// <summary>
    /// Bound property ends with the value. See the remarks on <see cref="Operator"/>: in memory this is a
    /// culture sensitive comparison, against a database it follows the column's collation
    /// </summary>
    EndsWith,

    /// <summary>
    /// Bound property has a value and it does not end with the value given. A null does not match, no more than
    /// it does for EndsWith. See the remarks on <see cref="Operator"/> for that, and for how the comparison rules
    /// differ between in-memory and database evaluation
    /// </summary>
    DoesNotEndWith,

    /// <summary>
    /// Bound property contains the value. See the remarks on <see cref="Operator"/>: in memory this is an ordinal
    /// comparison, unlike StartsWith and EndsWith, and against a database it follows the column's collation
    /// </summary>
    Contains,

    /// <summary>
    /// Bound property has a value and it does not contain the value given. A null does not match, no more than it
    /// does for Contains. See the remarks on <see cref="Operator"/> for that, and for how the comparison rules
    /// differ between in-memory and database evaluation
    /// </summary>
    DoesNotContain,

    /// <summary>Any child condition matches. Over no children, matches nothing</summary>
    Or,

    /// <summary>Every child condition matches. Over no children, matches everything</summary>
    And,

    /// <summary>
    /// The single child condition does not match. Note that this negates the whole test, the null guard included,
    /// so unlike the negative operators it brings the null rows back: "!(Alias == 'Ghost')" matches a row with no
    /// alias, where "Alias != 'Ghost'" does not. See the remarks on <see cref="Operator"/>
    /// </summary>
    Not,

    /// <summary>
    /// Bound property matches the regular expression given as the value. Strings only, and the one operator that
    /// does not work everywhere: see the note on where it runs, below.
    /// <para>
    /// A null does not match, as it does not for the substring operators. See <see cref="DoesNotMatch"/> for the
    /// negative of it, which is not the same question as <see cref="Not"/> around it.
    /// </para>
    /// <para>
    /// <b>Where it runs.</b> Unlike every other operator, this one is not available on every provider, because
    /// there is no regular expression in standard SQL and each provider answers for itself:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>In memory</b> it is .NET's own <see cref="System.Text.RegularExpressions.Regex"/>, bounded by
    /// <see cref="Inquiry{T}.MatchTimeout"/>, which is set per entity type.
    /// </description></item>
    /// <item><description>
    /// <b>SQLite</b> translates it to the REGEXP operator, which Microsoft.Data.Sqlite implements with .NET's own
    /// Regex, so it matches what memory matches.
    /// </description></item>
    /// <item><description>
    /// <b>PostgreSQL</b> translates it to the '~' operator, which is POSIX ARE rather than .NET: lookarounds,
    /// lazy quantifiers and named groups are not the same language, so a pattern using them can match different
    /// rows there than it does in memory.
    /// </description></item>
    /// <item><description>
    /// <b>SQL Server</b> does not translate it at all, and the query fails when it is built rather than returning
    /// the wrong rows. There is no fallback: evaluating it on the client would mean fetching every row.
    /// </description></item>
    /// </list>
    /// <para>
    /// The pattern is a value like any other, so it reaches the database as a parameter rather than being written
    /// into the statement.
    /// </para>
    /// </summary>
    IsMatch,

    /// <summary>
    /// Bound property has a value and it does not match the regular expression given as the value. Strings only,
    /// and it runs exactly where <see cref="IsMatch"/> runs, under the same limits: read that first.
    /// <para>
    /// A null does not match, no more than it does for <see cref="IsMatch"/>, which is what makes this the
    /// negative operator rather than a negation. "Alias DoesNotMatch '^G'" asks for the minions whose alias is
    /// there and does not begin with G; "!(Alias IsMatch '^G')" also brings back the ones with no alias at all.
    /// Both are useful, so neither is normalised into the other. See the remarks on <see cref="Operator"/>.
    /// </para>
    /// </summary>
    DoesNotMatch,
}
