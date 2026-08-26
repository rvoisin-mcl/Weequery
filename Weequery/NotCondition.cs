using Weequery.Interfaces;

namespace Weequery;

/// <summary>
/// <inheritdoc cref="INotCondition"/>
/// </summary>
public class NotCondition : INotCondition
{
    /// <summary>
    /// Always <see cref="Weequery.Operator.Not"/>
    /// </summary>
    public Operator Operator { get; init; }

    /// <summary>
    /// The one condition being negated, held as a list so this shares a shape with the other containers
    /// </summary>
    public List<ICondition> Conditions { get; init; }

    /// <summary>
    /// ctor
    /// </summary>
    /// <param name="op"></param>
    /// <param name="condition">the condition to negate</param>
    /// <exception cref="WeequeryException"></exception>
    public NotCondition(Operator op, ICondition condition)
    {
        switch (op)
        {
            case Operator.Not:
                break;

            default:
                throw new WeequeryException($"Operation '{op}' cannot be represented by {nameof(NotCondition)}");
        }

        WeequeryException.ThrowIfNull(condition);

        List<ICondition> useConditions = new() { condition };

        Operator = op;
        Conditions = useConditions;
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
    /// Flatten to the serializable shape, the negated condition included
    /// </summary>
    /// <returns></returns>
    /// <exception cref="WeequeryException">the tree nests deeper than <see cref="ConditionNesting.MaxDepth"/></exception>
    public PackedCondition Pack()
    {
        return new PackedCondition(this);
    }
}