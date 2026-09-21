namespace Weequery;

/// <summary>
/// The table a query looks its fields up in, keyed by binding key.
/// </summary>
/// <remarks>
/// <para>
/// Keys are case-insensitive
/// </para>
/// </remarks>
internal static class BindingLookup
{
    /// <summary>
    /// How a field name from a caller is matched against a binding key
    /// </summary>
    internal static readonly StringComparer KeyComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// An empty lookup, with the key comparison every lookup has to share
    /// </summary>
    /// <typeparam name="TClass"></typeparam>
    /// <returns></returns>
    internal static Dictionary<string, Binding<TClass>> Create<TClass>()
    {
        return new(KeyComparer);
    }

    /// <summary>
    /// Take a field name apart into the key and the index it may carry: "Tallies[apples]" is the binding Tallies
    /// read at "apples", and "Pay" is the binding Pay.
    /// </summary>
    /// <remarks>
    /// For the two places an index arrives written into the field itself rather than beside it: an operand naming
    /// another bound property, and a sort. A condition keeps the two apart in <see cref="Interfaces.IBound.Index"/>,
    /// having somewhere to put it.
    /// </remarks>
    /// <param name="field"></param>
    /// <returns>the key, and the index or null</returns>
    /// <exception cref="WeequeryException">the brackets do not close, or the index is empty</exception>
    internal static (string Key, string? Index) SplitIndex(string field)
    {
        var open = field.IndexOf('[');
        if (open < 0) { return (field, null); }

        if (!field.EndsWith(']')) { throw new WeequeryException($"'{field}' has a '[' that is never closed"); }

        var index = field[(open + 1)..^1];
        if (index.Length == 0) { throw new WeequeryException($"'{field}' has an empty index"); }

        return (field[..open], index);
    }

    /// <summary>
    /// The binding a field name asks for, indexed where the name says so.
    /// </summary>
    /// <remarks>
    /// One place, so an operand and a sort refuse an unbound field with the same words a condition does, and
    /// index one the same way.
    /// </remarks>
    /// <typeparam name="TClass"></typeparam>
    /// <param name="bindings"></param>
    /// <param name="field">a binding key, optionally with an index after it</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">no binding claimed the key, or it cannot be indexed that way</exception>
    internal static Binding<TClass> Resolve<TClass>(Dictionary<string, Binding<TClass>> bindings, string field)
    {
        var (key, index) = SplitIndex(field);

        if (!bindings.TryGetValue(key, out var binding)) { throw new WeequeryException($"Unbound field: '{key}'"); }

        return (index is null) ? binding : binding.Indexed(index);
    }
}
