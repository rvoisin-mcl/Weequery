namespace Weequery;

/// <summary>
/// A field name taken apart into the key it names and the index it is taken at, where it carries one:
/// "Tallies[apples]" is the key Tallies at the index apples. See <see cref="ConditionFunctions.SplitIndex"/>,
/// which is how one is made.
/// </summary>
/// <remarks>
/// A readonly struct, and deconstructs, so it costs a caller nothing the pair of values it replaced did not and
/// reads the same way: <c>var (key, index) = ConditionFunctions.SplitIndex(field);</c>
/// </remarks>
/// <param name="Key">the binding key, matched without regard to case</param>
/// <param name="Index">the index the field is taken at, or null where the field carries none</param>
public readonly record struct IndexedField(string Key, string? Index);
