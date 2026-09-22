using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Text;

namespace Weequery.Bindings;

/// <summary>
/// The bindings a set of <see cref="BindingRequest"/> builds, kept for the life of the process so a declared set
/// is only resolved once rather than once per query.
/// </summary>
/// <remarks>
/// <para>
/// One cache per entity type, What it holds is immutable: a <see cref="Binding{TClass}"/> is an expression tree and a 
/// parameter, both of which are values, so a kept set is safe to hand to any number of queries on any number of threads.
/// </para>
/// <para>
/// <b>Treat a returned set as read only.</b> Modification will affect any other query using the same cached set,
/// see <see cref="Inquiry{T}.BindProperties"/>, which copies.
/// </para>
/// </remarks>
/// <typeparam name="T">the entity the bindings are against</typeparam>
internal static class BindingSetCache<T> where T : class
{
    /// <summary>
    /// The binding sets built for T, keyed by the requests that produced them.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Dictionary<string, Binding<T>>> BindingSets = new(); // { RequestKey, Bindings }

    /// <summary>
    /// How many distinct binding sets to hold, arbitrary. 
    /// Exists to prevent cache from growing without limit. Past it, bindings are built per call.
    /// </summary>
    private const int MaxCachedBindingSets = 64;

    /// <summary>
    /// The bindings for a set of requests, built once and kept
    /// </summary>
    /// <param name="bindingRequests"></param>
    /// <param name="parameter">the shared parameter every binding for this type hangs off</param>
    /// <returns>a lookup that must be treated as read only, since it is shared</returns>
    /// <exception cref="WeequeryException">a request names a property that cannot be bound, or two claim one key</exception>
    [RequiresDynamicCode(AotMessages.RuntimeGenerics)]
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
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
    /// Build a key that will uniquely identify a request set
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
