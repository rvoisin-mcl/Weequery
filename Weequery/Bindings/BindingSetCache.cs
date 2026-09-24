using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Text;
using Weequery.Interfaces;

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

        var bindings = Build(requests, parameter);

        // Two threads meeting on the same new set both build one, and either will do
        if (BindingSets.Count < MaxCachedBindingSets) { BindingSets.TryAdd(key, bindings); }

        return bindings;
    }

    /// <summary>
    /// Build the bindings for a set of requests, without looking in or adding to the cache
    /// </summary>
    /// <param name="requests"></param>
    /// <param name="parameter">the shared parameter every binding for this type hangs off</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">a request names a property that cannot be bound, or two claim one key</exception>
    [RequiresDynamicCode(AotMessages.RuntimeGenerics)]
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    internal static Dictionary<string, Binding<T>> Build(IEnumerable<BindingRequest> requests, ParameterExpression parameter)
    {
        // Against the shared parameter, so these compose with anything else bound for this type
        Dictionary<string, Binding<T>> bindings = BindingLookup.Create<T>();
        foreach (var bindingDefinition in requests)
        {
            Binding<T>.Create(parameter, bindingDefinition.PropertyPath, bindings, bindingDefinition.Key, bindingDefinition.Use);
        }

        return bindings;
    }

    /// <summary>
    /// What <see cref="Inquiry{T}.BindResolve"/> built for T, keyed by what it was asked for rather than by what
    /// the walk returned.
    /// </summary>
    private static readonly ConcurrentDictionary<ResolutionKey, ResolvedBindingSet<T>> ResolvedSets = new();

    /// <summary>
    /// How many distinct resolutions to hold, arbitrary, as <see cref="MaxCachedBindingSets"/> is.
    /// </summary>
    private const int MaxCachedResolvedSets = 64;

    /// <summary>
    /// The bindings a <see cref="Inquiry{T}.BindResolve"/> call produces, built once per distinct set of arguments
    /// and kept.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keyed on the arguments rather than on the requests they resolve to, which is what makes a hit cheap: the
    /// walk itself, and the collections inside it, are what a resolution costs, and a key built from the requests
    /// could only be built after walking. Both halves are kept, so a repeated call walks nothing and builds nothing.
    /// </para>
    /// <para>
    /// The settings are compared by what they hold, see <see cref="ResolutionKey"/>, so settings built fresh per
    /// request find the entry the first one made.
    /// </para>
    /// </remarks>
    /// <param name="maxDepth">already bounded to [0, 16]</param>
    /// <param name="settings">already defaulted</param>
    /// <param name="use">what the bindings may be used for</param>
    /// <param name="resolve">builds the set on a miss</param>
    /// <returns>a set that must be treated as read only, since it is shared</returns>
    internal static ResolvedBindingSet<T> ForResolution(int maxDepth, BindingResolutionSettings settings, BindingUse use, Func<ResolvedBindingSet<T>> resolve)
    {
        var key = new ResolutionKey(maxDepth, settings, use);
        if (ResolvedSets.TryGetValue(key, out var cached)) { return cached; }

        var resolved = resolve();

        // Two threads meeting on the same new set both build one, and either will do
        if (ResolvedSets.Count < MaxCachedResolvedSets) { ResolvedSets.TryAdd(key, resolved); }

        return resolved;
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

/// <summary>
/// Everything a <see cref="Inquiry{T}.BindResolve"/> call binds, see <see cref="BindingSetCache{T}.ForResolution"/>.
/// </summary>
/// <param name="Properties">the property bindings, keyed; shared, so read only</param>
/// <param name="Collections">the collection bindings, each immutable</param>
/// <typeparam name="T">the entity the bindings are against</typeparam>
internal sealed record ResolvedBindingSet<T>(Dictionary<string, Binding<T>> Properties, IReadOnlyList<ICollectionBinding<T>> Collections);
