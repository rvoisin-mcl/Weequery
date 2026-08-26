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
}
