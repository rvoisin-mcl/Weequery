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
    /// What the binding may be used for, see <see cref="BindingUse"/>. Everything unless the request says
    /// otherwise
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

        // The derived key as well as the provided one
        WeequeryException.ThrowIfNotBindingKey(Key, nameof(key));
    }

    /// <summary>
    /// ctor
    /// </summary>
    /// <param name="pathSegments">PropertyPath, should [PropertyName], or [Parent,Child,Grandchild,...]</param>
    /// <param name="key">[OPT] Key to use for binding, if not specified, the joined path is used.</param>
    /// <param name="use">[OPT] what it may be used for, all three by default</param>
    /// <exception cref="WeequeryException"></exception>
    public BindingRequest(string[] pathSegments, string? key, BindingUse use = BindingUse.All)
    {
        Use = use;

        WeequeryException.ThrowIfNullOrEmpty(pathSegments);
        WeequeryException.ThrowIfNotNullButEmpty(key);
        WeequeryException.ThrowIfNotBindingKey(key);

        PropertyPath = string.Join(".", pathSegments);

        WeequeryException.ThrowIfNullOrEmpty(PropertyPath, nameof(pathSegments)); // catch pathologic BindingRequest([""], null) case

        Key = key ?? PropertyPath;

        // The derived key as well as the provided one
        WeequeryException.ThrowIfNotBindingKey(Key, nameof(key));
    }
}
