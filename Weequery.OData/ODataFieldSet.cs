namespace Weequery.OData;

/// <summary>
/// The allow-list: which keys a condition may name, and what each one means in the service.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="FieldSet{TField}"/> of <see cref="ODataField"/>, which is where the adding, the finding and the
/// refusing live. There is no metadata document here to read, so the set is declared rather than derived, and a
/// condition naming a key it does not hold is refused.
/// </para>
/// <code>
/// var fields = new ODataFieldSet
/// {
///     new("Name",  "Name"),
///     new("Pay",   "Salary",  ODataFieldKind.Number),
///     new("Hired", "HiredOn", ODataFieldKind.Date),
///     new("City",  "Lair/Address/City"),
/// };
/// </code>
/// </remarks>
public sealed class ODataFieldSet : FieldSet<ODataField>
{
    /// <summary>An empty set, to be added to</summary>
    public ODataFieldSet()
    { }

    /// <summary>A set holding these</summary>
    /// <param name="fields"></param>
    /// <exception cref="WeequeryException">a field is null, or two claim one key</exception>
    public ODataFieldSet(IEnumerable<ODataField> fields) : base(fields)
    { }

    /// <summary>
    /// Declare a field from its parts, for the caller who would rather not name the type
    /// </summary>
    /// <param name="key">the name a caller writes</param>
    /// <param name="field">the property's path, slash separated</param>
    /// <param name="kind">[OPT] what it holds, a string by default</param>
    /// <param name="collection">[OPT] the collection it lives inside, where it does</param>
    /// <returns>this, so it can be chained</returns>
    /// <exception cref="WeequeryException">the key is already taken</exception>
    public ODataFieldSet Add(string key, string field, ODataFieldKind kind = ODataFieldKind.String, string? collection = null)
    {
        Add(new ODataField(key, field, kind, collection));

        return this;
    }
}
