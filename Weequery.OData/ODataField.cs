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
    /// A collection navigation property, which is the only thing a quantifier can be asked about and the only
    /// thing a comparison cannot. See <see cref="ODataFilter"/>.
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
    /// <summary>Whether the string functions apply to it</summary>
    internal bool IsTextual { get { return (Kind == ODataFieldKind.String) || (Kind == ODataFieldKind.Enum); } }

    /// <summary>Whether it can be put in order, so whether the relational operators apply</summary>
    internal bool IsOrderable { get { return (Kind != ODataFieldKind.Boolean) && (Kind != ODataFieldKind.Collection); } }
}
