using Weequery.Interfaces;

namespace Weequery;

/// <summary>
/// <inheritdoc cref="IConjunctionCondition"/>
/// </summary>
public class ConjunctionCondition : IConjunctionCondition
{
    /// <summary>
    /// How the children are joined, so <see cref="Weequery.Operator.And"/> or <see cref="Weequery.Operator.Or"/>
    /// </summary>
    public Operator Operator { get; init; }

    /// <summary>
    /// The conditions being joined. If none provided, And matches everything and Or matches nothing.
    /// </summary>
    public List<ICondition> Conditions { get; init; }

    /// <summary>
    /// ctor
    /// </summary>
    /// <param name="op"></param>
    /// <param name="components"></param>
    /// <exception cref="WeequeryException"></exception>
    public ConjunctionCondition(Operator op, List<ICondition> components)
    {
        switch (op)
        {
            case Operator.Or:
            case Operator.And:
                break;

            default:
                throw new WeequeryException($"Operation '{op}' cannot be represented by {nameof(ConjunctionCondition)}");
        }

        WeequeryException.ThrowIfNull(components);

        Operator = op;
        Conditions = [.. components]; // Copy instead of keep

        for (int index = 0; index < Conditions.Count; index++)
        {
            if (Conditions[index] is null) { throw new WeequeryException($"{nameof(components)}[{index}] is null"); }
        }
    }

    /// <summary>
    /// Renders in the query language. See <see cref="ConditionFunctions.ToQuery"/> for the round-trippable form,
    /// which is the same text except that this will not throw on a condition the language cannot express.
    /// </summary>
    /// <returns></returns>
    public override string ToString()
    {
        return QueryWriter.Describe(this);
    }

    /// <summary>
    /// Flatten to the serializable shape, children and all
    /// </summary>
    /// <returns></returns>
    /// <exception cref="WeequeryException">the tree nests deeper than <see cref="ConditionNesting.MaxDepth"/></exception>
    public PackedCondition Pack()
    {
        return new PackedCondition(this);
    }
}
