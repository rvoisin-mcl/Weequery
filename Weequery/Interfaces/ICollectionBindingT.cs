using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace Weequery.Interfaces;

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
/// An <see cref="IBinding"/> rather than an <see cref="IValueBinding"/>, which is the whole of the difference.
/// It has a path, an accessor and a null guard like any binding, and it has no single value, so an operator has
/// nothing to compare and only a quantifier has anything to ask. That is why the expression builders take an
/// <see cref="IValueBinding"/>: a collection cannot reach them to be turned away, because it cannot be handed
/// to them at all.
/// </para>
/// <para>
/// Generic in the entity rather than the element, which the outer <see cref="Inquiry{T}"/> does not know.
/// </para>
/// </remarks>
/// <typeparam name="TClass">the entity the collection hangs off</typeparam>
internal interface ICollectionBinding<TClass> : IBinding
{
    /// <summary>The key the collection was bound under</summary>
    string Key { get; }

    /// <summary>What the collection holds, for an error that has to name it</summary>
    Type ElementType { get; }

    /// <summary>
    /// If the inner allow-list bound this key, so if a condition inside the quantifier can name it.
    /// </summary>
    /// <remarks>
    /// Asked rather than resolved, by the one thing that has to know a field is missing without wanting it to
    /// fail: pruning an unbound field out of a query, see <see cref="InquirySettings.IgnoreUnboundFields"/>. The
    /// inner set is the collection's own, so nothing outside it can answer this.
    /// </remarks>
    /// <param name="key">a key, which may carry an index</param>
    /// <returns></returns>
    bool Binds(string key);

    /// <summary>
    /// The inner allow-list, described. See <see cref="Inquiry{T}.ListBindings"/>.
    /// </summary>
    /// <remarks>
    /// Asked of the collection because only the collection can answer it: the inner set is keyed by
    /// <c>Binding{TElement}</c>, and TElement is what this interface exists to hide from the outer
    /// <see cref="Inquiry{T}"/>. So the collection does the projecting and hands back something the outside can
    /// hold.
    /// </remarks>
    /// <returns>every key nameable inside a quantifier, ordered</returns>
    IReadOnlyList<BoundBinding> ListElements();

    /// <summary>
    /// The same collection with part of its inside taken away, see
    /// <see cref="Inquiry{T}.RemoveBinding(string, BindingUse)"/>.
    /// </summary>
    /// <remarks>
    /// Asked of the collection for the reason <see cref="ListElements"/> is: the inner set is keyed by
    /// <c>Binding{TElement}</c> and TElement is what this interface hides, so only the collection can rebuild
    /// itself around a smaller one.
    /// </remarks>
    /// <param name="remove">what to take out, asked of each key the inner set holds</param>
    /// <returns>
    /// the collection unchanged where nothing matched, a smaller one where some did, and null where the inner set
    /// would be left empty, an empty one being unable to answer any condition at all
    /// </returns>
    ICollectionBinding<TClass>? Without(Func<string, bool> remove);

    /// <summary>
    /// The test for a quantifier over this collection, as an expression on the entity's own parameter.
    /// </summary>
    /// <param name="quantifier"><see cref="Operator.Any"/>, <see cref="Operator.All"/> or <see cref="Operator.None"/></param>
    /// <param name="inner">the condition scoped to one element</param>
    /// <returns>a predicate on the entity, total: it is never null and needs no guard of its own</returns>
    /// <exception cref="WeequeryException">the inner condition names something the inner set did not bind</exception>
    [RequiresDynamicCode(AotMessages.RuntimeGenerics)]
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    Expression<Func<TClass, bool>> Quantify(Operator quantifier, ICondition inner);
}
