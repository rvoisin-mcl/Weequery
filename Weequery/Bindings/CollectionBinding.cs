using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using Weequery.Builders;
using Weequery.Interfaces;

namespace Weequery.Bindings;

/// <summary>
/// A collection bound on <typeparamref name="TClass"/>, holding the bindings for its elements.
/// </summary>
/// <typeparam name="TClass">the entity the collection hangs off</typeparam>
/// <typeparam name="TElement">what the collection holds</typeparam>
internal sealed class CollectionBinding<TClass, TElement> : ICollectionBinding<TClass>
    where TElement : class
{
    public string Key { get; }

    public Type ElementType { get { return typeof(TElement); } }

    // The IBinding half is the collection's own accessor, which this has had all along as Collection: where it
    // lives, how to reach it, and the guard that says reaching it is safe. Guarded() below is already built out
    // of the last two, so none of this is new behaviour, only a name for what was already true.

    /// <inheritdoc/>
    public string PropertyPath { get { return Collection.PropertyPath; } }

    /// <inheritdoc/>
    public Expression Accessor { get { return Collection.Accessor; } }

    /// <inheritdoc/>
    public Type PropertyType { get { return Collection.PropertyType; } }

    /// <inheritdoc/>
    public bool AccessorIsNullable { get { return Collection.AccessorIsNullable; } }

    /// <inheritdoc/>
    public ParameterExpression Parameter { get { return Collection.Parameter; } }

    /// <inheritdoc/>
    public bool RequiresNullCheck { get { return Collection.RequiresNullCheck; } }

    /// <inheritdoc/>
    public Expression NotNullCheck { get { return Collection.NotNullCheck; } }

    /// <summary>How to reach the collection from the entity, guards and all</summary>
    private Binding<TClass> Collection { get; }

    /// <summary>
    /// What may be asked about an element, keyed the way every other lookup is.
    /// <para>
    /// Built against <see cref="Binding{TClass}"/>'s shared parameter for TElement, which is what lets the inner
    /// predicate be a lambda over the element: every binding for a type hangs off one parameter, so a body
    /// assembled from several of them composes.
    /// </para>
    /// </summary>
    private Dictionary<string, Binding<TElement>> Inner { get; }

    internal CollectionBinding(string key, Binding<TClass> collection, Dictionary<string, Binding<TElement>> inner)
    {
        Key = key;
        Collection = collection;
        Inner = inner;
    }

    /// <inheritdoc/>
    public bool Binds(string key)
    {
        return Inner.ContainsKey(BindingLookup.SplitIndex(key).Key);
    }

    /// <inheritdoc/>
    public ICollectionBinding<TClass>? Without(Func<string, bool> remove)
    {
        WeequeryException.ThrowIfNull(remove);

        var kept = Inner
            .Where(entry => !remove(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value, BindingLookup.KeyComparer);

        // Nothing matched, so the collection is returned rather than rebuilt: a binding is immutable and shared,
        // and a copy of one that did not change is a copy for nothing
        if (kept.Count == Inner.Count) { return this; }

        // Nothing left inside is nothing to quantify over, which is the state BindCollection refuses to be built
        // in. The caller takes the collection out rather than keeping one that can answer no condition
        return (kept.Count == 0) ? null : new CollectionBinding<TClass, TElement>(Key, Collection, kept);
    }

    /// <inheritdoc/>
    public IReadOnlyList<BoundBinding> ListElements()
    {
        // Test rather than the grant the binding carries. An element is only ever tested: the inner set has no
        // BindingUse to give and the bindings in it are built with the default, so reporting what is stored
        // would promise a sort and a projection that nothing here can perform
        return
        [
            .. Inner
                .Select(entry => new BoundBinding(entry.Key, entry.Value.PropertyPath, BindingUse.Test))
                .OrderBy(bound => bound.Key, BindingLookup.KeyComparer)
        ];
    }

    /// <inheritdoc/>
    [RequiresDynamicCode(AotMessages.RuntimeGenerics)]
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    public Expression<Func<TClass, bool>> Quantify(Operator quantifier, ICondition inner)
    {
        WeequeryException.ThrowIfNull(inner);

        // The element's own predicate, built from the inner allow-list exactly as an outer one is built from the
        // outer. An unbound field inside is refused here, naming the field, the same as anywhere else.
        var predicate = ExpressionBuilder.BuildExpression(Inner, inner);

        var method = Method(quantifier).MakeGenericMethod(typeof(TElement));

        // Enumerable.Any and Enumerable.All want IEnumerable<TElement>, and the collection may be declared as
        // anything that is one
        var source = Expression.Convert(Collection.Accessor, typeof(IEnumerable<TElement>));

        Expression test = Expression.Call(null, method, source, predicate);

        if (quantifier == Operator.None) { test = Expression.Not(test); }

        return Expression.Lambda<Func<TClass, bool>>(Guarded(test, quantifier), Collection.Parameter);
    }

    /// <summary>
    /// The quantifier is total, so a collection that is not there has to answer rather than throw.
    /// </summary>
    /// <remarks>
    /// Reading a null collection would throw where the query runs in memory, and the answer for one is not in
    /// doubt: it holds no elements. So Any is false for it and All and None are true, which is what those mean of
    /// nothing and what both LINQ and SQL say. The guard is written so the short circuit does the work, which is
    /// also the form a provider translates.
    /// <para>
    /// Nothing is added where the path cannot produce a null, which is the common case of a collection reached
    /// directly off the entity and never assigned null.
    /// </para>
    /// </remarks>
    private Expression Guarded(Expression test, Operator quantifier)
    {
        if (!Collection.RequiresNullCheck) { return test; }

        // "there is a collection AND some element matches" for Any, and "there is no collection OR every element
        // matches" for the two that are true of nothing
        return (quantifier == Operator.Any)
            ? Expression.AndAlso(Collection.NotNullCheck, test)
            : Expression.OrElse(Expression.Not(Collection.NotNullCheck), test);
    }

    /// <summary>
    /// Any and None are both Enumerable.Any, one of them negated; All is its own.
    /// </summary>
    private static MethodInfo Method(Operator quantifier)
    {
        var name = (quantifier == Operator.All) ? nameof(Enumerable.All) : nameof(Enumerable.Any);

        // The two argument overload, which is the one taking a predicate rather than asking if there is
        // anything at all
        return typeof(Enumerable)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(candidate => candidate.Name.Equals(name, StringComparison.Ordinal) && (candidate.GetParameters().Length == 2));
    }
}
