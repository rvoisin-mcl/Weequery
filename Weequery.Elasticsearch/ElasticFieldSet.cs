namespace Weequery.Elasticsearch;

/// <summary>
/// The allow-list: which keys a condition may name, and what each one means in the index.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="FieldSet{TField}"/> of <see cref="ElasticField"/>, which is where the adding, the finding and
/// the refusing live. There is no entity here to walk and no mapping to read, so the set is declared rather than
/// derived, and a condition naming a key it does not hold is refused.
/// </para>
/// <code>
/// var fields = new ElasticFieldSet
/// {
///     new("Name",  "name",         ElasticFieldKind.Text),
///     new("Alias", "alias.keyword"),
///     new("Pay",   "salary",       ElasticFieldKind.Number),
///     new("Hired", "hired_on",     ElasticFieldKind.Date),
/// };
/// </code>
/// </remarks>
public sealed class ElasticFieldSet : FieldSet<ElasticField>
{
    /// <summary>An empty set, to be added to</summary>
    public ElasticFieldSet()
    { }

    /// <summary>A set holding these</summary>
    /// <param name="fields"></param>
    /// <exception cref="WeequeryException">a field is null, or two claim one key</exception>
    public ElasticFieldSet(IEnumerable<ElasticField> fields) : base(fields)
    { }

    /// <summary>
    /// Declare a field from its parts, for the caller who would rather not name the type
    /// </summary>
    /// <param name="key">the name a caller writes</param>
    /// <param name="field">the field's path in the index</param>
    /// <param name="kind">[OPT] what it holds, keyword by default</param>
    /// <param name="nested">[OPT] the nested path it lives under, where it does</param>
    /// <returns>this, so it can be chained</returns>
    /// <exception cref="WeequeryException">the key is already taken</exception>
    public ElasticFieldSet Add(string key, string field, ElasticFieldKind kind = ElasticFieldKind.Keyword, string? nested = null)
    {
        Add(new ElasticField(key, field, kind, nested));

        return this;
    }
}
