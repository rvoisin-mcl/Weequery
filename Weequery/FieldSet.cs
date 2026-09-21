using System.Collections;

namespace Weequery;

/// <summary>
/// Something a <see cref="FieldSet{TField}"/> can hold: anything that knows the name a caller writes for it.
/// </summary>
public interface IFieldKey
{
    /// <summary>The name a caller writes, matched without regard to case</summary>
    string Key { get; }
}

/// <summary>
/// An allow-list declared rather than derived: which keys a caller may name, and what each one means.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to an <see cref="Inquiry{T}"/>'s bindings, for the targets that have no entity to walk. There
/// is no CLR type here and no mapping to read, so what a key means is stated instead of resolved — but it grants
/// exactly as little, and a condition naming a key it does not hold is refused the same way.
/// </para>
/// <para>
/// Keys are matched without regard to case, as they are everywhere else in Weequery, and two entries cannot
/// claim one key. What a field carries beyond its key is the target's business, which is what
/// <typeparamref name="TField"/> is for.
/// </para>
/// <code>
/// public sealed class MyFieldSet : FieldSet&lt;MyField&gt;
/// {
///     public MyFieldSet() { }
///     public MyFieldSet(IEnumerable&lt;MyField&gt; fields) : base(fields) { }
/// }
/// </code>
/// </remarks>
/// <typeparam name="TField">what is declared for a key</typeparam>
public abstract class FieldSet<TField> : IEnumerable<TField> where TField : IFieldKey
{
    private readonly Dictionary<string, TField> Fields = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// An empty set, to be added to
    /// </summary>
    protected FieldSet()
    { }

    /// <summary>
    /// A set holding these
    /// </summary>
    /// <param name="fields"></param>
    /// <exception cref="WeequeryException">a field is null, or two claim one key</exception>
    protected FieldSet(IEnumerable<TField> fields)
    {
        WeequeryException.ThrowIfNull(fields);

        foreach (var field in fields) { Add(field); }
    }

    /// <summary>
    /// Declare a field. Present so a collection initializer works, which is the readable way to write a set.
    /// </summary>
    /// <param name="field"></param>
    /// <returns>this, so it can be chained</returns>
    /// <exception cref="WeequeryException">the field is null, or its key is already taken</exception>
    public FieldSet<TField> Add(TField field)
    {
        WeequeryException.ThrowIfNull(field);
        WeequeryException.ThrowIfNullOrEmpty(field.Key, nameof(field));

        if (Fields.ContainsKey(field.Key)) { throw new WeequeryException($"A field is already declared for '{field.Key}'"); }

        Fields[field.Key] = field;

        return this;
    }

    /// <summary>The field a key means, or null where nothing declared it</summary>
    /// <param name="key"></param>
    /// <returns></returns>
    public TField? Find(string? key)
    {
        return ((key is not null) && Fields.TryGetValue(key, out var field)) ? field : default;
    }

    /// <summary>Whether a key is declared</summary>
    /// <param name="key"></param>
    /// <returns></returns>
    public bool Has(string? key)
    {
        return (key is not null) && Fields.ContainsKey(key);
    }

    /// <summary>
    /// The field a key means, refusing what nobody declared.
    /// </summary>
    /// <param name="key"></param>
    /// <param name="what">what is being attempted, so the message says where the key came from</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">nothing declared the key</exception>
    public TField Resolve(string? key, string what)
    {
        return Find(key) ?? throw new WeequeryException($"Unbound field: '{key}' is not declared in the {GetType().Name}, so it cannot be {what}");
    }

    /// <summary>How many fields are declared</summary>
    public int Count { get { return Fields.Count; } }

    /// <summary>The keys, as they were declared</summary>
    public IEnumerable<string> Keys { get { return from declared in Fields.Values select declared.Key; } }

    /// <inheritdoc/>
    public IEnumerator<TField> GetEnumerator()
    {
        return Fields.Values.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
