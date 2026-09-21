namespace Weequery.Elasticsearch;

/// <summary>
/// What a field holds in the index, which decides both how a value is written into the query and which operators
/// may be asked of it.
/// </summary>
/// <remarks>
/// Elasticsearch has no schema a query can be checked against from here, so this is the caller telling us what
/// their mapping says. Getting it wrong produces a query that runs and finds nothing rather than one that fails,
/// which is the failure mode this exists to avoid.
/// </remarks>
public enum ElasticFieldKind
{
    /// <summary>
    /// A <c>keyword</c> field, or any field indexed whole rather than analysed. Compared with <c>term</c>, which
    /// is exact and case sensitive.
    /// </summary>
    Keyword,

    /// <summary>
    /// A <c>text</c> field, which is analysed, so it holds tokens rather than the string that was indexed.
    /// </summary>
    /// <remarks>
    /// <c>term</c> against one of these is the classic Elasticsearch mistake: it looks for the whole string among
    /// the tokens and finds nothing. Equality here becomes <c>match_phrase</c>, which is the nearest honest
    /// answer, and it is the analyser rather than this library that decides what counts as equal. Where you want
    /// exactness, index a <c>keyword</c> sub-field and name that instead.
    /// </remarks>
    Text,

    /// <summary>Any numeric field. Values are written as JSON numbers rather than as strings.</summary>
    Number,

    /// <summary>A <c>boolean</c> field. Written as a JSON boolean, and it takes only the equality operators.</summary>
    Boolean,

    /// <summary>
    /// A <c>date</c> field. Values are passed through as text for Elasticsearch to parse against the mapping's
    /// own format, which is what lets "2024-01-15" and a full ISO 8601 timestamp both work.
    /// </summary>
    Date,
}

/// <summary>
/// One entry of the allow-list: a key a caller may name, and the field in the index it means.
/// </summary>
/// <remarks>
/// <para>
/// The same idea as a Weequery binding, and it grants the same one thing: a condition naming a key nobody
/// declared is refused. What it maps to is the field's path in the index, which is often not what the caller
/// calls it.
/// </para>
/// <code>
/// new ElasticField("Alias", "alias.keyword", ElasticFieldKind.Keyword)
/// new ElasticField("Pay", "salary", ElasticFieldKind.Number)
/// new ElasticField("LairName", "assignments.lair.name", ElasticFieldKind.Keyword, Nested: "assignments")
/// </code>
/// </remarks>
/// <param name="Key">the name a caller writes, matched without regard to case</param>
/// <param name="Field">the field's path in the index</param>
/// <param name="Kind">what it holds, see <see cref="ElasticFieldKind"/></param>
/// <param name="Nested">
/// [OPT] the <c>nested</c> path this field lives under, where it does. Only meaningful inside a quantifier, and
/// every field inside one has to name the same path, see <see cref="ElasticQuery"/>.
/// </param>
public record ElasticField(string Key, string Field, ElasticFieldKind Kind = ElasticFieldKind.Keyword, string? Nested = null)
    : IFieldKey
{
    /// <summary>Whether this field can be asked the substring and pattern operators</summary>
    internal bool IsTextual { get { return (Kind == ElasticFieldKind.Keyword) || (Kind == ElasticFieldKind.Text); } }

    /// <summary>Whether this field can be put in order, so whether the range operators apply</summary>
    internal bool IsOrderable { get { return Kind != ElasticFieldKind.Boolean; } }
}
