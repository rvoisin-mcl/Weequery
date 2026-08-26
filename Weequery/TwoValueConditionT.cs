using Weequery.Interfaces;

namespace Weequery;

/// <summary>
/// <inheritdoc cref="ITwoValueCondition"/>
/// </summary>
/// <remarks>
/// Either end can name another bound property instead of being a value, and they can be mixed:
/// "Pay IsBetween ([Floor], 20000)" is a range with one end from the row and one from the query. A key is a name,
/// so only a condition over string can carry one, see <see cref="ConditionValue{T}"/>.
/// </remarks>
/// <typeparam name="T">what the operands are, which is string for a condition that arrived as text</typeparam>
public class TwoValueCondition<T> : BoundCondition, ITwoValueCondition<T>
{
    /// <summary>
    /// <inheritdoc cref="ITwoValueCondition{T}.Value1"/>
    /// </summary>
    public ConditionValue<T> Value1 { get; init; }

    /// <summary>
    /// <inheritdoc cref="ITwoValueCondition{T}.Value2"/>
    /// </summary>
    public ConditionValue<T> Value2 { get; init; }

    /// <summary>
    /// ctor, over a range of values
    /// </summary>
    /// <param name="op"><see cref="Operator.IsBetween"/> or <see cref="Operator.IsNotBetween"/></param>
    /// <param name="field">the binding key to test</param>
    /// <param name="value1">the low end, inclusive</param>
    /// <param name="value2">the high end, inclusive</param>
    /// <exception cref="WeequeryException">
    /// the field is missing, a value is missing, or the operator takes some other number of values
    /// </exception>
    public TwoValueCondition(Operator op, string field, T value1, T value2)
        : this(op, field, ConditionValue.Raw(value1), ConditionValue.Raw(value2))
    {
    }

    /// <summary>
    /// ctor, over a range whose ends may be values or other bound properties
    /// </summary>
    /// <param name="op"><see cref="Operator.IsBetween"/> or <see cref="Operator.IsNotBetween"/></param>
    /// <param name="field">the binding key on the left of the comparison</param>
    /// <param name="value1">the low end, inclusive</param>
    /// <param name="value2">the high end, inclusive</param>
    /// <exception cref="WeequeryException">
    /// the field is missing, a value is missing, a named property is not readable as a name or is not carried as
    /// text, or the operator takes some other number of values
    /// </exception>
    public TwoValueCondition(Operator op, string field, ConditionValue<T> value1, ConditionValue<T> value2)
        : base(op, field, ConditionShape.TwoValue)
    {
        Value1 = Validate(op, field, value1, 0, 2);
        Value2 = Validate(op, field, value2, 1, 2);
    }

    /// <summary>
    /// <inheritdoc cref="IBoundCondition.StringifyOperands"/>
    /// </summary>
    /// <returns></returns>
    public override List<ConditionValue<string>> StringifyOperands()
    {
        return [Value1.Stringify(), Value2.Stringify()];
    }
}
