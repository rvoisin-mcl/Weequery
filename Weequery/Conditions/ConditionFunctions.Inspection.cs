using Weequery.Bindings;
using Weequery.Interfaces;

namespace Weequery;

// Reading a condition back out: which fields it names, which operators it uses, and whether a data source
// that cannot run one of them would have to refuse it, see OperatorSupport.
public static partial class ConditionFunctions
{
    /// <summary>
    /// Split a field into the key it names and the index it is taken at, if one is present.
    /// </summary>
    /// <param name="field">a field name, which may carry an index</param>
    /// <returns>the key, and the index or null where there is none</returns>
    /// <exception cref="WeequeryException">the field is null or empty</exception>
    public static IndexedField SplitIndex(string field)
    {
        WeequeryException.ThrowIfNullOrEmpty(field);

        return BindingLookup.SplitIndex(field);
    }

    /// <summary>
    /// Every unique field a condition names, including the ones its operands name
    /// </summary>
    /// <remarks>
    /// <para>
    /// What a caller needs to answer "which bindings does this query actually use",, and for anyone auditing which
    /// of them a given query is allowed to touch, see <see cref="BindingUse"/>.
    /// </para>
    /// <para>
    /// <b>Stops at a quantifier</b>, taking the collection's key and not the fields inside it. Those resolve
    /// against the collection's own allow-list rather than the entity's, see
    /// <see cref="Inquiry{T}.BindCollection"/>, so putting them in one flat list would say a field is bound on the
    /// entity when it is not. Walk into <see cref="QuantifiedCondition.Condition"/> and call this again to get
    /// them.
    /// </para>
    /// <para>
    /// Keys carry their index where a condition tested one, so "Tallies[apples]" comes back as it was written.
    /// Names are compared without case-insensitivily, the first variaion seen is the one used
    /// </para>
    /// </remarks>
    /// <param name="condition">null gives an empty list</param>
    /// <returns>never null, in the order the fields were first met</returns>
    public static List<string> FieldsUsed(this ICondition? condition)
    {
        Dictionary<string, string> seen = new(BindingLookup.KeyComparer);

        Collect(condition, seen, 0);

        return [.. seen.Values];
    }

    /// <summary>
    /// Gather one level of <see cref="FieldsUsed"/>.
    /// </summary>
    /// <remarks>
    /// Stops at <see cref="ConditionNesting.MaxDepth"/>
    /// </remarks>
    private static void Collect(ICondition? condition, Dictionary<string, string> seen, int depth)
    {
        if ((condition is null) || ConditionNesting.IsTooDeep(depth)) { return; }

        if (condition is PackedCondition packed)
        {
            Keep(seen, Indexed(packed.Field, packed.Index));

            // A quantifier's children are scoped to the collection's own allow-list, not the entity's
            if (QuantifiedCondition.IsQuantifier(packed.Operator)) { return; }

            foreach (var operand in packed.Values.Where(operand => operand.NamesProperty))
            {
                Keep(seen, operand.Value);
            }

            foreach (var child in packed.Conditions)
            {
                Collect(child, seen, depth + 1);
            }

            return;
        }

        // Names a collection and holds a condition scoped to one of its elements. The collection is a field of
        // the entity; what is inside it is not, stop here
        if (condition is QuantifiedCondition quantified)
        {
            Keep(seen, quantified.Field);
            return;
        }

        if (condition is IBound bound)
        {
            Keep(seen, Indexed(bound.Field, bound.Index));

            if (condition is IBoundCondition valued)
            {
                // An operand can use bindings on both sides of the operator
                foreach (var operand in valued.StringifyOperands().Where(operand => operand.NamesProperty))
                {
                    Keep(seen, operand.Value);
                }
            }

            return;
        }

        if (condition is IConditionContainer<ICondition> container)
        {
            foreach (var child in container.Conditions)
            {
                Collect(child, seen, depth + 1);
            }
        }
    }

    /// <summary>
    /// Every operator a condition uses, including the ones inside a quantifier.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The counterpart of <see cref="FieldsUsed"/>
    /// </para>
    /// <para>
    /// Structural operators count too, <see cref="Operator.And"/>, <see cref="Operator.Or"/> and
    /// <see cref="Operator.Not"/>, since something has to evaluate those as well. See
    /// <see cref="OperatorSupport"/>, which is what this exists for.
    /// </para>
    /// </remarks>
    /// <param name="condition">null gives an empty list</param>
    /// <returns>each operator once, in the order it was first met; never null</returns>
    public static List<Operator> OperatorsUsed(this ICondition? condition)
    {
        List<Operator> seen = []; // return in order discovered

        foreach (var used in Used(condition, 0))
        {
            if (!seen.Contains(used.Operator)) { seen.Add(used.Operator); }
        }

        return seen;
    }

    /// <summary>
    /// The first operator in a condition that <paramref name="support"/> does not allow, and the field it was
    /// used on where it named one.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="OperatorsUsed"/> so the refusal can name where it happened, which a list of
    /// operators cannot. Stops at the first, for the reason every other validation stops at the first: see the
    /// remarks on <see cref="ValidationResult"/>.
    /// </remarks>
    /// <param name="condition"></param>
    /// <param name="support"></param>
    /// <returns>null where every operator used is allowed, which includes the condition that is null</returns>
    internal static UsedOperator? FirstUnsupported(ICondition? condition, OperatorSupport support)
    {
        foreach (var used in Used(condition, 0))
        {
            if (!support.Allows(used.Operator)) { return used; }
        }

        return null;
    }

    /// <summary>
    /// Walk a condition, yielding each operator as it is met with the field it was used on.
    /// </summary>
    /// <remarks>
    /// Stops at <see cref="ConditionNesting.MaxDepth"/>, as the field walk does, so a tree deep enough to
    /// overflow the stack is refused for its depth rather than by falling over here.
    /// </remarks>
    /// <param name="condition"></param>
    /// <param name="depth">levels entered to get to this condition</param>
    /// <returns>one entry per condition met, in the order met, with duplicates left in</returns>
    private static IEnumerable<UsedOperator> Used(ICondition? condition, int depth)
    {
        if ((condition is null) || ConditionNesting.IsTooDeep(depth)) { yield break; }

        yield return new UsedOperator(condition.Operator, (condition as IBound)?.Field);

        // Two shapes of container, since a packed tree holds packed children, see IConditionContainer
        IEnumerable<ICondition> children = condition switch
        {
            IConditionContainer<ICondition> container => container.Conditions,
            IConditionContainer<PackedCondition> packed => packed.Conditions,
            _ => [],
        };

        foreach (var child in children)
        {
            foreach (var used in Used(child, depth + 1)) { yield return used; }
        }
    }

    /// <summary>
    /// A field with its index put back on, which is how a key carrying one is written everywhere else
    /// </summary>
    private static string Indexed(string field, string? index)
    {
        return (index is null) ? field : $"{field}[{index}]";
    }

    /// <summary>
    /// Keep the first spelling of a name, since two that differ only in case are one field
    /// </summary>
    private static void Keep(Dictionary<string, string> seen, string field)
    {
        if (!string.IsNullOrEmpty(field)) { seen.TryAdd(field, field); }
    }
}
