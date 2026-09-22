using System.Diagnostics.CodeAnalysis;
namespace Weequery.Bindings;

/// <summary>
/// A cachable lookup table for bindings, key are case-insensitive
/// </summary>
/// <remarks>
/// </remarks>
internal static class BindingLookup
{
    /// <summary>
    /// Comparison to use for key matching
    /// </summary>
    internal static readonly StringComparer KeyComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// An empty lookup
    /// </summary>
    /// <typeparam name="TClass"></typeparam>
    /// <returns></returns>
    internal static Dictionary<string, Binding<TClass>> Create<TClass>()
    {
        return new(KeyComparer);
    }

    /// <summary>
    /// Crack a field name apart into the key and optional index it may carry
    /// </summary>
    /// <param name="field"></param>
    /// <returns>the key, and the index or null</returns>
    /// <exception cref="WeequeryException">the brackets do not close, or the index is empty</exception>
    internal static IndexedField SplitIndex(string field)
    {
        var open = field.IndexOf('[');
        if (open < 0) { return new IndexedField(field, null); }

        if (!field.EndsWith(']')) { throw new WeequeryException(WeequeryError.PathInvalid, $"'{field}' has a unclosed '['"); }

        var index = field[(open + 1)..^1];
        if (index.Length == 0) { throw new WeequeryException(WeequeryError.PathInvalid, $"'{field}' has an empty index"); }

        return new IndexedField(field[..open], index);
    }

    /// <summary>
    /// The binding a field name asks for, indexed where the name says so.
    /// </summary>
    /// <typeparam name="TClass"></typeparam>
    /// <param name="bindings"></param>
    /// <param name="field">a binding key, optionally with an index</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">no binding claimed the key, or it cannot be indexed that way</exception>
    [RequiresDynamicCode(AotMessages.RuntimeGenerics)]
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    internal static Binding<TClass> Resolve<TClass>(Dictionary<string, Binding<TClass>> bindings, string field)
    {
        var split = SplitIndex(field);

        if (!bindings.TryGetValue(split.Key, out var binding)) { throw new WeequeryException(WeequeryError.UnboundField, $"Unbound field: '{split.Key}'"); }

        return (split.Index is null) ? binding : binding.Indexed(split.Index);
    }

    /// <summary>
    /// The name a field was bound under
    /// </summary>
    /// <remarks>
    /// <para>
    /// Since keys are case-insensitive, we could lose the originally binding name
    /// </para>
    /// </remarks>
    /// <param name="bindings"></param>
    /// <param name="field">a key, which may carry an index</param>
    /// <returns>the field as bound, index and all, or the field as given where nothing claimed it</returns>
    internal static string CanonicalKey<TClass>(Dictionary<string, Binding<TClass>> bindings, string field)
    {
        var split = SplitIndex(field);

        var bound = bindings.Keys.FirstOrDefault(candidate => KeyComparer.Equals(candidate, split.Key)) ?? split.Key;

        return (split.Index is null) ? bound : $"{bound}[{split.Index}]";
    }
}
