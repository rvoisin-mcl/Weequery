namespace Weequery;

/// <summary>
/// Something a <see cref="FieldSet{TField}"/> can hold, anything that knows the name a caller will use
/// </summary>
public interface IFieldKey
{
    /// <summary>The name a caller writes</summary>
    string Key { get; }
}
