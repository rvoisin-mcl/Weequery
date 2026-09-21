using Weequery.Interfaces;

namespace Weequery;

/// <summary>
/// Remove the parts of a condition that name use an unbound field out of it. For callers that would prefer to return
/// a superset of the filtered data, instead of refusing. See <see cref="Inquiry{T}.IgnoreUnboundFields"/>
/// </summary>
/// <remarks>
/// <para>
/// Only unbound fields go. A field that is bound but does not grant <see cref="BindingUse.Condition"/>
/// is a deliberate choice about what a caller may ask, so those are still refused.
/// </para>
/// </remarks>
internal static class ConditionPruner
{
    /// <summary>
    /// The condition with every unanswerable part removed.
    /// </summary>
    /// <typeparam name="TClass"></typeparam>
    /// <param name="condition"></param>
    /// <param name="bindings">the entity's allow-list</param>
    /// <param name="collections">the bound collections, which a quantifier resolves against</param>
    /// <param name="dropped">called once with each field taken out, in the order they were met</param>
    /// <returns>null where nothing of it survived, which means no filtering at all</returns>
    /// <exception cref="WeequeryException">the tree nests deeper than <see cref="ConditionNesting.MaxDepth"/></exception>
    internal static ICondition? Prune<TClass>(
        ICondition? condition,
        Dictionary<string, Binding<TClass>> bindings,
        Dictionary<string, ICollectionBinding<TClass>> collections,
        Action<string> dropped)
    {
        return Prune(condition, key => bindings.ContainsKey(BindingLookup.SplitIndex(key).Key), collections, dropped, 0);
    }

    /// <summary>
    /// One level of the walk.
    /// </summary>
    /// <param name="condition"></param>
    /// <param name="isBound">if a key is one the allow-list in scope holds, index and all</param>
    /// <param name="collections">
    /// the bound collections, or null inside a quantifier, where a nested one could not resolve anyway
    /// </param>
    /// <param name="dropped">called once with each field taken out</param>
    /// <param name="depth">levels of nesting already stepped into on the way here</param>
    private static ICondition? Prune<TClass>(
        ICondition? condition,
        Func<string, bool> isBound,
        Dictionary<string, ICollectionBinding<TClass>>? collections,
        Action<string> dropped,
        int depth)
    {
        if (condition is null) { return null; }

        if (condition is PackedCondition packed)
        {
            return Prune(packed.Unpack(), isBound, collections, dropped, depth);
        }

        if (condition is QuantifiedCondition quantified)
        {
            // Nothing bound the collection, so there is nothing to quantify over and the whole thing goes
            if ((collections is null) || (!collections.TryGetValue(quantified.Field, out var collection)))
            {
                dropped(quantified.Field);
                return null;
            }

            // The inside is its own allow-list, so the inner condition is pruned against that rather than the
            // entity's. A nested quantifier has no collections to resolve against and prunes away.
            var inner = Prune(quantified.Condition, collection.Binds, (Dictionary<string, ICollectionBinding<TClass>>?)null, dropped, ConditionNesting.Descend(depth));

            // A quantifier with no test left is not "every element", it is a question that was not asked
            return (inner is null) ? null : new QuantifiedCondition(quantified.Operator, quantified.Field, inner);
        }

        if (condition is IBound bound)
        {
            return Keeps(condition, bound, isBound, dropped) ? condition : null;
        }

        if (condition is IConjunctionCondition conjunction)
        {
            var nested = ConditionNesting.Descend(depth);

            var kept = (from child in conjunction.Conditions
                        select Prune(child, isBound, collections, dropped, nested) into pruned
                        where pruned is not null
                        select pruned!).ToList();

            // Every operand is gone, so the conjunction is empty and goes with them.
            return (kept.Count == 0) ? null : new ConjunctionCondition(conjunction.Operator, kept);
        }

        if (condition is INotCondition negation)
        {
            var operand = Prune(negation.Conditions.FirstOrDefault(), isBound, collections, dropped, ConditionNesting.Descend(depth));

            // The negation of nothing is nothing, not everything
            return (operand is null) ? null : new NotCondition(Operator.Not, operand);
        }

        // Something this does not recognise is left alone, so whatever would have refused it still does
        return condition;
    }

    /// <summary>
    /// If a comparison survives: its own field is bound, and so is every property it compares against.
    /// </summary>
    private static bool Keeps(ICondition condition, IBound bound, Func<string, bool> isBound, Action<string> dropped)
    {
        if (!isBound(bound.Field))
        {
            dropped(bound.Field);
            return false;
        }

        if (condition is not IBoundCondition valued) { return true; }

        var missing = valued.StringifyOperands().Where(operand => operand.NamesProperty && (!isBound(operand.Value))).ToList();
        foreach (var operand in missing)
        {
            dropped(operand.Value);
        }

        return missing.Count == 0;
    }
}
