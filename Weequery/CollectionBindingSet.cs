using System.Linq.Expressions;

namespace Weequery;

/// <summary>
/// What may be asked about one element of a bound collection, declared when the collection is bound. See
/// <see cref="Inquiry{T}.BindCollection"/>.
/// </summary>
/// <remarks>
/// <para>
/// The allow-list one level down. It reads like the outer one on purpose, and refuses the same things for the
/// same reasons, because it is the same idea: nothing about an element is answerable until it is named here.
/// </para>
/// <code>
/// inner =&gt; inner
///     .BindProperty(assignment =&gt; assignment.LairID)
///     .BindProperty(assignment =&gt; assignment.Lair.Name, "LairName")
///     .BindProperty("Lair.Capacity", "LairCapacity")
/// </code>
/// <para>
/// What it does not have is paging, sorting or conditions: an element is tested, not queried. Nor constants,
/// which belong to the query rather than to one level of it and can be bound on the outer
/// <see cref="Inquiry{T}"/> where the whole condition can reach them.
/// </para>
/// </remarks>
/// <typeparam name="TElement">what the collection holds</typeparam>
public sealed class CollectionBindingSet<TElement>
    where TElement : class
{
    /// <summary>
    /// The one parameter every binding for this element type hangs off, which is what lets a predicate built
    /// from several of them compose. Shared per type, exactly as <see cref="Inquiry{T}"/> shares its own.
    /// </summary>
    private static readonly ParameterExpression SharedParameter = Expression.Parameter(typeof(TElement));

    internal Dictionary<string, Binding<TElement>> Bindings { get; } = BindingLookup.Create<TElement>();

    /// <summary>How many bindings have been made, so an empty set can be refused</summary>
    internal int Count { get { return Bindings.Count; } }

    /// <summary>
    /// Bind the property the selector reaches. Keyed by the property path where no key is given.
    /// </summary>
    /// <typeparam name="TProperty"></typeparam>
    /// <param name="selector"></param>
    /// <param name="key">[OPT] the name a caller uses inside the quantifier</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">the key is not one, or is already taken</exception>
    public CollectionBindingSet<TElement> BindProperty<TProperty>(Expression<Func<TElement, TProperty>> selector, string? key = null)
    {
        Binding<TElement>.Create(SharedParameter, selector, Bindings, key);

        return this;
    }

    /// <summary>
    /// Bind the property the path names, which may be dotted and may index, exactly as it may on the outside.
    /// </summary>
    /// <param name="path">eg. "Lair.Capacity", or "Tags[0]"</param>
    /// <param name="key">[OPT] the name a caller uses inside the quantifier</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">the path does not resolve, or the key is not one, or is already taken</exception>
    public CollectionBindingSet<TElement> BindProperty(string path, string? key = null)
    {
        WeequeryException.ThrowIfNullOrEmpty(path);
        WeequeryException.ThrowIfNotNullButEmpty(key);
        WeequeryException.ThrowIfNotBindingKey(key);

        Binding<TElement>.Create(SharedParameter, path, Bindings, key);

        return this;
    }

    /// <summary>
    /// Bind the property reached by following the selector and then the segments after it, for a path a selector
    /// cannot write on its own. See <see cref="Inquiry{T}.BindProperty{TProperty}(Expression{Func{T, TProperty}}, string[], string?, BindingUse, ValueConverter)"/>.
    /// </summary>
    /// <typeparam name="TProperty"></typeparam>
    /// <param name="selector">as far as the compiler can follow</param>
    /// <param name="segments">the rest of the path, in order</param>
    /// <param name="key">[OPT] the name a caller uses inside the quantifier</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException"></exception>
    public CollectionBindingSet<TElement> BindProperty<TProperty>(Expression<Func<TElement, TProperty>> selector, string[] segments, string? key = null)
    {
        Binding<TElement>.Create(SharedParameter, selector, segments, Bindings, key);

        return this;
    }

    /// <summary>
    /// Bind a set of requests, resolved once for the process and kept, exactly as
    /// <see cref="Inquiry{T}.BindProperties"/> does.
    /// </summary>
    /// <param name="bindingRequests"></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">a request does not resolve, or two claim one key</exception>
    public CollectionBindingSet<TElement> BindProperties(IEnumerable<BindingRequest> bindingRequests)
    {
        WeequeryException.ThrowIfNull(bindingRequests);

        foreach (var request in bindingRequests)
        {
            Binding<TElement>.Create(SharedParameter, request.PropertyPath, Bindings, request.Key);
        }

        return this;
    }
}
