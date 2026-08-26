namespace Weequery.Interfaces;

/// <summary>
/// <inheritdoc cref="IOneValueCondition"/>
/// </summary>
/// <typeparam name="T">what the operand is, before it is stringified for transport</typeparam>
public interface IOneValueCondition<T> : IOneValueCondition
{
    /// <summary>
    /// What the property is compared against
    /// </summary>
    ConditionValue<T> Value { get; }
}
