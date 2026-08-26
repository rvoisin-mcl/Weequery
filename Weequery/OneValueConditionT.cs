using Weequery.Interfaces;

namespace Weequery;

/// <summary>
/// <inheritdoc cref="IOneValueCondition"/>
/// </summary>
/// <remarks>
/// The operand can name another bound property instead of being a value, so "Pay &gt; [Salary]" is this condition
/// as much as "Pay &gt; 10000" is. A key is a name, so only a condition over string can carry one, see
/// <see cref="ConditionValue{T}"/>.
/// </remarks>
/// <typeparam name="T">what the operand is, which is string for a condition that arrived as text</typeparam>
public class OneValueCondition<T> : BoundCondition, IOneValueCondition<T>
{
    /// <summary>
    /// What the property is compared against
    /// </summary>
    public ConditionValue<T> Value { get; init; }

    /// <summary>
    /// ctor, comparing against a value
    /// </summary>
    /// <param name="op">one of the operators that take a single value</param>
    /// <param name="field">the binding key to test</param>
    /// <param name="value"></param>
    /// <exception cref="WeequeryException">
    /// the field is missing, the value is missing, or the operator takes some other number of values
    /// </exception>
    public OneValueCondition(Operator op, string field, T value)
        : this(op, field, ConditionValue.Raw(value))
    {
    }

    /// <summary>
    /// ctor, comparing against a value or against another bound property
    /// </summary>
    /// <param name="op">one of the operators that take a single value</param>
    /// <param name="field">the binding key on the left of the comparison</param>
    /// <param name="value"></param>
    /// <exception cref="WeequeryException">
    /// the field is missing, the value is missing, a named property is not readable as a name or is not carried as
    /// text, or the operator takes some other number of values
    /// </exception>
    public OneValueCondition(Operator op, string field, ConditionValue<T> value)
        : base(op, field, ConditionShape.OneValue)
    {
        Value = Validate(op, field, value, 0, 1);
    }

    /// <summary>
    /// <inheritdoc cref="IBoundCondition.StringifyOperands"/>
    /// </summary>
    /// <returns></returns>
    public override List<ConditionValue<string>> StringifyOperands()
    {
        return [Value.Stringify()];
    }
}
