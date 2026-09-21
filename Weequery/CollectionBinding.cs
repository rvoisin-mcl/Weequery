using System.Linq.Expressions;
using System.Reflection;
using Weequery.Builders;
using Weequery.Interfaces;

namespace Weequery;

/// <summary>
/// A bound collection, and the allow-list for what may be asked about one of its elements.
/// </summary>
/// <remarks>
/// <para>
/// The one binding that holds bindings. A quantified condition names the collection and carries a condition
/// scoped to the element, so the element's fields have to resolve against something, and that something is
/// declared separately: binding a collection says nothing about what is reachable inside it, and the inner set
/// is an allow-list in its own right.
/// </para>
/// <para>
/// Generic in the element type, which the outer <see cref="Inquiry{T}"/> does not know, so it is reached through
/// <see cref="ICollectionBinding{TClass}"/>.
/// </para>
/// </remarks>
/// <typeparam name="TClass">the entity the collection hangs off</typeparam>
internal interface ICollectionBinding<TClass>
{
    /// <summary>The key the collection was bound under</summary>
    string Key { get; }

    /// <summary>What the collection holds, for an error that has to name it</summary>
    Type ElementType { get; }

    /// <summary>
    /// Whether the inner allow-list bound this key, so whether a condition inside the quantifier can name it.
    /// </summary>
    /// <remarks>
    /// Asked rather than resolved, by the one thing that has to know a field is missing without wanting it to
    /// fail: pruning an unbound field out of a query, see <see cref="Inquiry{TClass}.IgnoreUnboundFields"/>. The
    /// inner set is the collection's own, so nothing outside it can answer this.
    /// </remarks>
    /// <param name="key">a key, which may carry an index</param>
    /// <returns></returns>
    bool Binds(string key);

    /// <summary>
    /// The test for a quantifier over this collection, as an expression on the entity's own parameter.
    /// </summary>
    /// <param name="quantifier"><see cref="Operator.Any"/>, <see cref="Operator.All"/> or <see cref="Operator.None"/></param>
    /// <param name="inner">the condition scoped to one element</param>
    /// <returns>a predicate on the entity, total: it is never null and needs no guard of its own</returns>
    /// <exception cref="WeequeryException">the inner condition names something the inner set did not bind</exception>
    Expression<Func<TClass, bool>> Quantify(Operator quantifier, ICondition inner);
}

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

        // The two argument overload, which is the one taking a predicate rather than asking whether there is
        // anything at all
        return typeof(Enumerable)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(candidate => candidate.Name.Equals(name, StringComparison.Ordinal) && (candidate.GetParameters().Length == 2));
    }
}
