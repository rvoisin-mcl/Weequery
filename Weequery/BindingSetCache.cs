using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Text;

namespace Weequery;

/// <summary>
/// The bindings a set of <see cref="BindingRequest"/> builds, kept for the life of the process so a declared set
/// is resolved once rather than once per query.
/// </summary>
/// <remarks>
/// <para>
/// One cache per entity type, since the class is generic and a static in a generic class is per constructed
/// type. What it holds is immutable: a <see cref="Binding{TClass}"/> is an expression tree and a parameter, both
/// of which are values, so a kept set is safe to hand to any number of queries on any number of threads.
/// </para>
/// <para>
/// <b>Treat what comes back as read only.</b> It is shared, and an Inquiry that added to it would be adding to
/// every other query built from the same requests, see <see cref="Inquiry{T}.BindProperties"/>, which copies.
/// </para>
/// </remarks>
/// <typeparam name="T">the entity the bindings are against</typeparam>
internal static class BindingSetCache<T> where T : class
{
    /// <summary>
    /// The binding sets built for this entity type, keyed by the requests that produced them.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Dictionary<string, Binding<T>>> BindingSets = new();

    /// <summary>
    /// How many distinct binding sets to hold. Sets come from code, so an application has a handful and this is
    /// never reached; the cap is only here so that a caller composing sets dynamically cannot grow the cache
    /// without bound. Past it, bindings are built per call.
    /// </summary>
    private const int MaxCachedBindingSets = 64;

    /// <summary>
    /// The bindings for a set of requests, built once and kept, see <see cref="BindingSetCache{T}"/>.
    /// </summary>
    /// <param name="bindingRequests"></param>
    /// <param name="parameter">the shared parameter every binding for this type hangs off</param>
    /// <returns>a lookup that must be treated as read only, since it is shared</returns>
    /// <exception cref="WeequeryException">a request names a property that cannot be bound, or two claim one key</exception>
    internal static Dictionary<string, Binding<T>> For(IEnumerable<BindingRequest> bindingRequests, ParameterExpression parameter)
    {
        // Read once: the requests may be a lazy sequence, and the key has to describe the same set that gets built
        var requests = (bindingRequests as IReadOnlyList<BindingRequest>) ?? bindingRequests.ToList();

        var key = CacheKey(requests);
        if (BindingSets.TryGetValue(key, out var cached)) { return cached; }

        // Against the shared parameter, so these compose with anything else bound for this type
        Dictionary<string, Binding<T>> bindings = BindingLookup.Create<T>();
        foreach (var bindingDefinition in requests)
        {
            Binding<T>.Create(parameter, bindingDefinition.PropertyPath, bindings, bindingDefinition.Key, bindingDefinition.Use);
        }

        // Two threads meeting on the same new set both build one, and either will do
        if (BindingSets.Count < MaxCachedBindingSets) { BindingSets.TryAdd(key, bindings); }

        return bindings;
    }

    /// <summary>
    /// Describes a set of requests exactly, so two sets share an entry only when they would build the same
    /// bindings. The separators cannot appear in a path or a key, both of which are SQL names, dotted for a path.
    /// <para>
    /// The use is part of what a request builds, so it is part of what tells two sets apart: the same paths
    /// bound for filtering and bound for projection only are two different sets of bindings, and one cache
    /// entry cannot be both, see <see cref="BindingUse"/>.
    /// </para>
    /// </summary>
    /// <param name="requests"></param>
    /// <returns></returns>
    private static string CacheKey(IReadOnlyList<BindingRequest> requests)
    {
        StringBuilder builder = new();

        foreach (var request in requests)
        {
            builder.Append(request.PropertyPath).Append('>').Append(request.Key).Append('>').Append((int)request.Use).Append('|');
        }

        return builder.ToString();
    }

}
