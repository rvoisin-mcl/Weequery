using Weequery.Interfaces;

namespace Weequery;

/// <summary>
/// <inheritdoc cref="IMultipleValueCondition"/>
/// </summary>
/// <remarks>
/// Any of the operands can name another bound property instead of being a value, and they can be mixed:
/// "Pay IsIn (8000, [Cap])" tests against a value and a property at once. A key is a name, so only a condition
/// over string can carry one, see <see cref="ConditionValue{T}"/>.
/// </remarks>
/// <typeparam name="T">what the operands are, which is string for a condition that arrived as text</typeparam>
public class MultipleValueCondition<T> : BoundCondition, IMultipleValueCondition<T>
{
    /// <summary>
    /// Values to be tested
    /// </summary>
    public List<ConditionValue<T>> Values { get; init; }

    /// <summary>
    /// ctor, over a list of values
    /// </summary>
    /// <param name="op"><see cref="Operator.IsIn"/> or <see cref="Operator.IsNotIn"/></param>
    /// <param name="field">the binding key to test</param>
    /// <param name="values">copied, so the condition is not changed by later changes to the list passed in</param>
    /// <exception cref="WeequeryException">
    /// the field is missing, a value is missing, the operator takes some other number of values, or there are more
    /// than <see cref="ConditionFunctions.MaxValuesInList"/> of them
    /// </exception>
    public MultipleValueCondition(Operator op, string field, List<T> values)
        : this(op, field, Sourced(values))
    {
    }

    /// <summary>
    /// ctor, over a list whose members may be values or other bound properties
    /// </summary>
    /// <param name="op"><see cref="Operator.IsIn"/> or <see cref="Operator.IsNotIn"/></param>
    /// <param name="field">the binding key on the left of the comparison</param>
    /// <param name="values">copied, so the condition is not changed by later changes to the list passed in</param>
    /// <exception cref="WeequeryException">
    /// the field is missing, a value is missing, a named property is not readable as a name or is not carried as
    /// text, the operator takes some other number of values, or there are more than
    /// <see cref="ConditionFunctions.MaxValuesInList"/> of them
    /// </exception>
    public MultipleValueCondition(Operator op, string field, List<ConditionValue<T>> values)
        : base(op, field, ConditionShape.MultipleValue)
    {
        WeequeryException.ThrowIfNull(values);

        List<ConditionValue<T>> useValues = [.. values]; // copy, don't keep

        for (var index = 0; index < useValues.Count; index++)
        {
            Validate(op, field, useValues[index], index, useValues.Count);
        }

        ConditionFunctions.ValidateValueCount(op, field, useValues.Count);

        Values = useValues;
    }

    /// <summary>
    /// Wrap plain values as operands, for the ctor that takes them
    /// </summary>
    /// <param name="values"></param>
    /// <returns></returns>
    private static List<ConditionValue<T>> Sourced(List<T> values)
    {
        WeequeryException.ThrowIfNull(values);

        return [.. from value in values select ConditionValue.Raw(value)];
    }

    /// <summary>
    /// <inheritdoc cref="IBoundCondition.StringifyOperands"/>
    /// </summary>
    /// <returns></returns>
    public override List<ConditionValue<string>> StringifyOperands()
    {
        // Values is public, so what the list holds now is not necessarily what it was built from
        for (var index = 0; index < Values.Count; index++)
        {
            Validate(Operator, Field, Values[index], index, Values.Count);
        }

        return [.. from value in Values select value.Stringify()];
    }
}
