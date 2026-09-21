namespace Weequery;

/// <summary>
/// A field name taken apart into the binding key, and the optional index
/// </summary>
/// <param name="Key">the binding key, matched without regard to case</param>
/// <param name="Index">the index the field is taken at, or null where the field carries none</param>
public readonly record struct IndexedField(string Key, string? Index);
