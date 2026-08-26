namespace Weequery.Interfaces;

/// <summary>
/// <inheritdoc cref="ITwoValueCondition"/>
/// </summary>
/// <typeparam name="T">what the operands are, before they are stringified for transport</typeparam>
public interface ITwoValueCondition<T> : ITwoValueCondition
{
    /// <summary>
    /// The low end of the range, which the property is tested against inclusively
    /// </summary>
    ConditionValue<T> Value1 { get; }

    /// <summary>
    /// The high end of the range, which the property is tested against inclusively
    /// </summary>
    ConditionValue<T> Value2 { get; }
}
