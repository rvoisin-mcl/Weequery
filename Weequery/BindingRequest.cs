namespace Weequery;

/// <summary>
/// Use to create a list of bindings to be applied
/// </summary>
public class BindingRequest
{
    /// <summary>
    /// Path to the property on the entity, a single name or a dotted path such as "Lair.Capacity"
    /// </summary>
    public string PropertyPath { get; init; }

    /// <summary>
    /// The name a caller uses for it. Derived from path if not provided.
    /// </summary>
    public string Key { get; init; }

    /// <summary>
    /// What the binding may be used for, see <see cref="BindingUse"/>. All three unless the request says
    /// otherwise, so a list written before this existed means what it always meant.
    /// </summary>
    public BindingUse Use { get; init; } = BindingUse.All;

    /// <summary>
    /// ctor
    /// </summary>
    /// <param name="propertyPath">PropertyPath, should be a single property name, or Parent.Child.Grandchild.etc</param>
    /// <param name="key">
    /// [OPT] Key to use for binding, if not specified, PropertyPath will be used. A dotted path makes a legal key,
    /// a period being a legal key character, so a nested property needs no key of its own unless you want the
    /// caller to see a different name
    /// </param>
    /// <param name="use">[OPT] what it may be used for, all three by default</param>
    /// <exception cref="WeequeryException">the key, given or derived, is not one</exception>
    public BindingRequest(string propertyPath, string? key, BindingUse use = BindingUse.All)
    {
        Use = use;

        WeequeryException.ThrowIfNullOrEmpty(propertyPath);
        WeequeryException.ThrowIfNotNullButEmpty(key);
        WeequeryException.ThrowIfNotBindingKey(key);

        PropertyPath = propertyPath;
        Key = key ?? propertyPath;

        // The derived key as well as the given one. Binding would refuse it later either way, and a request is
        // usually a static declaration, so the error is worth more where the declaration is.
        WeequeryException.ThrowIfNotBindingKey(Key, nameof(key));
    }

    /// <summary>
    /// ctor
    /// </summary>
    /// <param name="propertyPath">PropertyPath, should [Property Name], or [Parent,Child,Grandchild,...]</param>
    /// <param name="key">
    /// [OPT] Key to use for binding, if not specified, the joined path is used, a period being a legal key
    /// character. The same key the string constructor derives for the same path, and the same one
    /// <see cref="Inquiry{T}.BindProperty{TProperty}(System.Linq.Expressions.Expression{System.Func{T, TProperty}}, string[], string, BindingUse, ValueConverter)"/>
    /// derives for the same segments, so a path keys alike however it is written
    /// </param>
    /// <param name="use">[OPT] what it may be used for, all three by default</param>
    /// <exception cref="WeequeryException"></exception>
    public BindingRequest(string[] propertyPath, string? key, BindingUse use = BindingUse.All)
    {
        Use = use;

        WeequeryException.ThrowIfNullOrEmpty(propertyPath);
        WeequeryException.ThrowIfNotNullButEmpty(key);
        WeequeryException.ThrowIfNotBindingKey(key);

        PropertyPath = string.Join(".", propertyPath);

        // Segments that join to nothing are the empty path the string constructor refuses, and are refused in
        // the same words: an array holding one empty segment says no more than an empty array does
        WeequeryException.ThrowIfNullOrEmpty(PropertyPath, nameof(propertyPath));

        Key = key ?? PropertyPath;

        // The derived key as well as the given one, the same as the string constructor above: the two derive the
        // same key for the same path, so they have to refuse the same ones.
        WeequeryException.ThrowIfNotBindingKey(Key, nameof(key));
    }
}
