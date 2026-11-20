using System.Diagnostics.CodeAnalysis;

namespace Weequery.OData;

/// <summary>
/// What a property holds, which decides how a value is written beside it and which operators it will take.
/// </summary>
/// <remarks>
/// OData writes a literal differently for almost every type, and gets it wrong loudly rather than quietly: a
/// string needs quotes, a Guid must not have them, and a service handed the wrong shape answers 400 rather than
/// nothing. There is no metadata document to read from here, so this is you saying what the EDM says.
/// </remarks>
[SuppressMessage("Naming", "CA1720:Identifiers should not contain type names", Justification = "These are the EDM type names, which is the whole job of this enum: String is Edm.String and Guid is Edm.Guid, as each member documents. Boolean, Date and DateTimeOffset are the same names and the rule does not flag them, so renaming the two it does would leave the enum half spelled after the spec and half after an analyser.")]
public enum ODataFieldKind
{
    /// <summary><c>Edm.String</c>. Written in single quotes, with any quote inside doubled.</summary>
    String,

    /// <summary>Any numeric type. Written bare.</summary>
    Number,

    /// <summary><c>Edm.Boolean</c>. Written as <c>true</c> or <c>false</c>.</summary>
    Boolean,

    /// <summary><c>Edm.Guid</c>. Written bare in OData v4, without the quotes v3 wanted.</summary>
    Guid,

    /// <summary><c>Edm.Date</c>, as <c>2024-01-15</c>. Written bare.</summary>
    Date,

    /// <summary><c>Edm.DateTimeOffset</c>, as <c>2024-01-15T09:00:00Z</c>. Written bare.</summary>
    DateTimeOffset,

    /// <summary><c>Edm.TimeOfDay</c>, as <c>09:00:00</c>. Written bare.</summary>
    TimeOfDay,

    /// <summary><c>Edm.Duration</c>, as an ISO 8601 duration. Written as <c>duration'P1D'</c>.</summary>
    Duration,

    /// <summary>
    /// An enumeration. Written qualified as <c>My.Namespace.Rank'High'</c> where the field names its
    /// <see cref="ODataField.EnumType"/>, and as a quoted string otherwise, which many services also accept.
    /// </summary>
    Enum,

    /// <summary>
    /// A collection, of objects or of values, which is the only thing a quantifier can be asked about and the only
    /// thing a comparison cannot. See <see cref="ODataFilter"/>, and <see cref="ODataField.ElementKind"/> for a
    /// collection of values.
    /// </summary>
    Collection,
}

/// <summary>
/// One entry of the allow-list: a key a caller may name, and the property it means in the service.
/// </summary>
/// <remarks>
/// <para>
/// The same idea as a Weequery binding, granting the same one thing: a condition naming a key nobody declared is
/// refused. What it maps to is the property's path, which OData separates with slashes rather than dots.
/// </para>
/// <code>
/// new ODataField("Alias", "Alias")
/// new ODataField("Pay", "Salary", ODataFieldKind.Number)
/// new ODataField("LairCity", "Lair/Address/City")
///
/// // a collection, and a field inside it, whose path is relative to the element
/// new ODataField("Assignments", "Assignments", ODataFieldKind.Collection)
/// new ODataField("LairName", "Lair/Name", Collection: "Assignments")
///
/// // a collection of values, whose element is named Value inside the quantifier
/// ODataField.ForValues("Tags", "Tags", ODataFieldKind.String)
/// </code>
/// </remarks>
/// <param name="Key">the name a caller writes, matched without regard to case</param>
/// <param name="Field">
/// the property's path, slash separated. Relative to the element where <paramref name="Collection"/> says this
/// lives inside one, since that is what a lambda's variable is prefixed to.
/// </param>
/// <param name="Kind">what it holds, see <see cref="ODataFieldKind"/></param>
/// <param name="Collection">
/// [OPT] the key of the collection this field lives inside, where it does. Only reachable within a quantifier
/// over that collection.
/// </param>
/// <param name="EnumType">
/// [OPT] the qualified name of the enumeration type, for <see cref="ODataFieldKind.Enum"/>. Without it an enum
/// value is written as a quoted string, which many services accept and the specification does not require them to.
/// </param>
public record ODataField(
    string Key,
    string Field,
    ODataFieldKind Kind = ODataFieldKind.String,
    string? Collection = null,
    string? EnumType = null)
    : IFieldKey
{
    /// <summary>
    /// What each element holds, for a <see cref="ODataFieldKind.Collection"/> of values rather than of objects:
    /// a <c>Collection(Edm.String)</c>, say, rather than a navigation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A collection of values has no field of an element to declare, so the element is named
    /// <see cref="QuantifiedCondition.ValueKey"/> inside the quantifier and written as the lambda's variable
    /// itself, with this deciding how a value beside it is written. <see cref="EnumType"/> applies to the
    /// elements where this is <see cref="ODataFieldKind.Enum"/>.
    /// <code>
    /// Tags Any (Value = 'red')      Tags/any(d1: d1 eq 'red')
    /// </code>
    /// </para>
    /// <para>
    /// Null for a collection of objects, whose fields are declared with <see cref="Collection"/>, and for every
    /// field that is not a collection at all. See <see cref="ForValues"/>, which sets both halves at once.
    /// </para>
    /// </remarks>
    public ODataFieldKind? ElementKind { get; init; }

    /// <summary>
    /// A collection of values, such as a <c>Collection(Edm.String)</c>, quantified over as
    /// <see cref="QuantifiedCondition.ValueKey"/>.
    /// </summary>
    /// <param name="key">the name a caller writes</param>
    /// <param name="field">the property's path, slash separated</param>
    /// <param name="elementKind">what each element holds</param>
    /// <param name="enumType">[OPT] the qualified enumeration type, where the elements are an enum</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">the elements are themselves declared a collection</exception>
    public static ODataField ForValues(string key, string field, ODataFieldKind elementKind, string? enumType = null)
    {
        if (elementKind == ODataFieldKind.Collection)
        {
            throw new WeequeryException(WeequeryError.BindingInvalid, $"'{key}' cannot hold collections: a collection of values holds values, and Weequery quantifies one level deep");
        }

        return new ODataField(key, field, ODataFieldKind.Collection, EnumType: enumType) { ElementKind = elementKind };
    }

    /// <summary>Whether the string functions apply to it</summary>
    internal bool IsTextual { get { return (Kind == ODataFieldKind.String) || (Kind == ODataFieldKind.Enum); } }

    /// <summary>Whether it can be put in order, so whether the relational operators apply</summary>
    internal bool IsOrderable { get { return (Kind != ODataFieldKind.Boolean) && (Kind != ODataFieldKind.Collection); } }
}
