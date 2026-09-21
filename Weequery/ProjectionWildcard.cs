namespace Weequery;

/// <summary>
/// What <c>*</c> and <c>Prefix.*</c> mean in a projection
/// </summary>
/// <remarks>
/// Three predicates rather than one expansion, because the two things that expand a projection do not expand it
/// against the same thing: <see cref="ProjectionBuilder{T}"/> works over bindings and filters them by
/// <see cref="BindingUse.Projection"/>, and <see cref="FieldSet{TField}"/> works over declared fields, which
/// have no use of their own. Sharing the rules rather than the loop is what keeps the two from drifting into
/// meaning different things by the same syntax.
/// </remarks>
internal static class ProjectionWildcard
{
    /// <summary>
    /// If the field stands for all bound fields
    /// </summary>
    /// <param name="field"></param>
    /// <returns></returns>
    internal static bool IsEverything(string field)
    {
        return field == Projection.Wildcard;
    }

    /// <summary>
    /// What a field stands for the whole of, or null where it names one thing.
    /// </summary>
    /// <remarks>
    /// The trailing dot is kept, which is the whole point of returning a prefix rather than a name: matching on
    /// "Lair" would sweep in a key called "Lairyard.Capacity", and matching on "Lair." cannot.
    /// </remarks>
    /// <param name="field"></param>
    /// <returns>"Lair." for "Lair.*", and null for anything else</returns>
    internal static string? Prefix(string field)
    {
        return field.EndsWith($".{Projection.Wildcard}", StringComparison.Ordinal)
            ? field[..^Projection.Wildcard.Length]
            : null;
    }

    /// <summary>
    /// Whether a key is one of the ones a prefix stands for.
    /// </summary>
    /// <remarks>
    /// Case-insensitive
    /// </remarks>
    /// <param name="key"></param>
    /// <param name="prefix">as <see cref="Prefix"/> returned it, with its dot still on</param>
    /// <returns></returns>
    internal static bool Under(string key, string prefix)
    {
        return key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
