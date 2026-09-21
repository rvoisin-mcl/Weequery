using Weequery.Interfaces;

namespace Weequery;

/// <summary>
/// Takes the parts of a condition that name a field nothing bound back out of it, for the caller who would rather
/// answer a stale filter than refuse it. See <see cref="Inquiry{T}.IgnoreUnboundFields"/>, which is the only
/// thing that asks for this.
/// </summary>
/// <remarks>
/// <para>
/// <b>Dropping always widens.</b> A test that is not there does not constrain, so removing one from an AND lets
/// more rows through, and a node whose every child was dropped is itself dropped rather than left standing as an
/// empty conjunction — an empty OR matches nothing, and "I could not read half your filter" is not a reason to
/// return no rows.
/// </para>
/// <para>
/// Carried to the extreme, a condition made entirely of unbound fields prunes to nothing at all, and a query with
/// no condition returns everything. That is the hazard of the whole idea and the reason it is off by default.
/// </para>
/// <para>
/// Only genuinely unbound fields go. A field that is bound and does not grant <see cref="BindingUse.Condition"/>
/// is a deliberate statement about what a caller may ask, and quietly ignoring one would undo the point of
/// making it, so those are still refused.
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
    /// <param name="isBound">whether a key is one the allow-list in scope holds, index and all</param>
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

        // A packed condition carries the same tree in a serializable shape, so prune what it unpacks to. What
        // comes back is an unpacked tree, which is what the expression builder would have made of it anyway.
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

            // Every operand went, so the conjunction says nothing and goes with them. Left standing it would be
            // an empty AND matching everything or an empty OR matching nothing, and neither is what "some of
            // this filter could not be read" should turn into.
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
    /// Whether a comparison survives: its own field is bound, and so is every property it compares against.
    /// </summary>
    /// <remarks>
    /// An operand naming a property is a read of that property, so one nothing bound makes the whole comparison
    /// unanswerable rather than merely short of a value. There is nothing left to compare with.
    /// </remarks>
    private static bool Keeps(ICondition condition, IBound bound, Func<string, bool> isBound, Action<string> dropped)
    {
        if (!isBound(bound.Field))
        {
            dropped(bound.Field);
            return false;
        }

        if (condition is not IBoundCondition valued) { return true; }

        // Reported by the name of the operand rather than of the comparison, since the operand is the part
        // nothing bound. Every missing one is named, so a comparison against two of them says so twice.
        var missing = valued.StringifyOperands().Where(operand => operand.NamesProperty && (!isBound(operand.Value))).ToList();

        foreach (var operand in missing) { dropped(operand.Value); }

        return missing.Count == 0;
    }
}
