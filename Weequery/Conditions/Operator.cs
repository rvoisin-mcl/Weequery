using System.Diagnostics.CodeAnalysis;

namespace Weequery;

/// <summary>
/// The operation a condition performs.
/// </summary>
/// <remarks>
/// <para>
/// Note on nulls. A null satisfies nothing except <see cref="IsNull"/>. Every other operator is built as "the
/// property has a value" ANDed with the test on that value. It is not "not equal to 5", it is unknown, exactly 
/// as a database treats it. 
/// </para>
/// <para>
/// This extends to a property reached through a nullable. A path may step into a Nullable&lt;T&gt;, so
/// "BirthDate.Year" on a DateTime? is legal, and the result behaves as a nullable in its own right even though
/// Year is an int: "BirthDate.Year IsNull" is true exactly when BirthDate is null, and a comparison on it simply
/// does not match a row whose BirthDate is null. A path through a null *reference* is the same story: "Lair.Name"
/// where the minion has no lair matches nothing, and "Lair.Name IsNull" asks if the lair is there. A database
/// answers that through the join, and guarding it here is what makes the two give the same answer.
/// </para>
/// <para>
/// Note on string comparison. Because evaluation of these will be dependent on the backing source, evaluation may
/// not be consistent between say, SQL-backed EF and an in-memory List
/// </para>
/// <list type="bullet">
/// <item><description>
/// In memory (LINQ to Objects, which includes anything built by Weequery&lt;T&gt;.BuildDelegate, or applied to an
/// IQueryable over an in-memory collection): the query says, see
/// <see cref="InquirySettings.StringComparison"/>. It covers equality, the ordering comparisons, the substring
/// family, the ranges and the IsIn family, so they all agree with each other. It is
/// <see cref="StringComparison.Ordinal"/> unless the query asked for something else.
/// </description></item>
/// <item><description>
/// Against a database through EF Core: each operator is translated to SQL (LIKE, instr, strpos, = and so on) and
/// the collation of the column decides the result, including if the match is case sensitive.
/// </description></item>
/// </list>
/// The default compares the characters that were stored, which is what a database does, so the two paths agree
/// about a given condition unless a query asks them not to. Asking for a culture is what parts them, and is
/// worth doing where a filter is meant to read the way a person reads: against a value whose first character is
/// a soft hyphen (U+00AD) followed by "Acme", both StartsWith 'Acme' and = 'Acme' match in memory under
/// <see cref="StringComparison.CurrentCulture"/>, because linguistic comparison treats a soft hyphen as
/// ignorable, and neither matches on SQLite. Case sensitivity varies by provider as well and is not something
/// this settles: LIKE is case insensitive for ASCII on SQLite but case sensitive on PostgreSQL.
/// </remarks>
[SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Operator is what this is called in the query language, in the error messages and on the wire, so renaming the type would leave the name everywhere it is actually read. A caller writes it as text far more often than as C#.")]
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
    /// Bound property contains the value. See the remarks on <see cref="Operator"/>: in memory this compares by
    /// the rules the query picked, the same as StartsWith and EndsWith, and against a database it follows the
    /// column's collation
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
    /// the wrong rows. There is no fallback: client evaluation would mean fetching every row.
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

    /// <summary>
    /// At least one element of a bound collection satisfies the condition inside it.
    /// <para>
    /// The quantifiers are the one family that takes a condition rather than values, so they carry a whole test
    /// scoped to the element: "Assignments Any (LairID = 5 AND IsPrimary = true)" asks for one assignment that is
    /// both, which is a different question from two separate tests over the collection. What may be asked about
    /// inside is its own allow-list, see <see cref="CollectionBindingSet{TElement}"/>.
    /// </para>
    /// <para>
    /// <b>Total rather than nullable.</b> Unlike every operator above, there is no unknown here: either some
    /// element matches or none does. A collection that is empty, or missing altogether, simply has no element
    /// that matches, so Any is false for it. That means the three quantifiers partition nothing and need no null
    /// guard of their own, and a caller does not have to think about a row whose collection was never loaded.
    /// </para>
    /// </summary>
    Any,

    /// <summary>
    /// Every element of a bound collection satisfies the condition inside it.
    /// <para>
    /// True of an empty or missing collection, which is what "every one of none" means and what both LINQ and SQL
    /// answer. Worth knowing before you use it as a filter: "All (IsActive = true)" keeps the rows with no
    /// assignments at all. Pair it with <see cref="Any"/> where you meant "has some, and they are all active".
    /// </para>
    /// </summary>
    All,

    /// <summary>
    /// No element of a bound collection satisfies the condition inside it, so the negation of <see cref="Any"/>.
    /// <para>
    /// Spelled as its own operator rather than left to <see cref="Not"/> because it reads better and because it
    /// puts the answer for an empty collection where you can see it: nothing matches, so None is true.
    /// </para>
    /// </summary>
    None,
}
