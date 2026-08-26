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
    /// ctor
    /// </summary>
    /// <param name="propertyPath">PropertyPath, should be a single property name, or Parent.Child.Grandchild.etc</param>
    /// <param name="key">[OPT] Key to use for binding, if not specified, PropertyPath will be used. NOTE: If propertyPath has links, then key MUST be provided, as the auto-generated key will be invalid</param>
    public BindingRequest(string propertyPath, string? key)
    {
        WeequeryException.ThrowIfNullOrEmpty(propertyPath);
        WeequeryException.ThrowIfNotNullButEmpty(key);
        WeequeryException.ThrowIfNotBindingKey(key);

        PropertyPath = propertyPath;
        Key = key ?? propertyPath;
    }

    /// <summary>
    /// ctor
    /// </summary>
    /// <param name="propertyPath">PropertyPath, should [Property Name], or [Parent,Child,Grandchild,...]</param>
    /// <param name="key">[OPT] Key to use for binding, if not specified, PropertyPath will be used. NOTE: If propertyPath has links, then key MUST be provided, as the auto-generated key will be invalid</param>
    /// <exception cref="WeequeryException"></exception>
    public BindingRequest(string[] propertyPath, string? key)
    {
        WeequeryException.ThrowIfNull(propertyPath);
        if (propertyPath.Length == 0) { throw new WeequeryException($"{nameof(propertyPath)} must contain at least one element"); }
        WeequeryException.ThrowIfNotNullButEmpty(key);
        WeequeryException.ThrowIfNotBindingKey(key);

        PropertyPath = string.Join(".", propertyPath);
        Key = key ?? propertyPath.Last();
    }
}
