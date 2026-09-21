namespace Weequery.Interfaces;

/// <summary>
/// <inheritdoc cref="IMultipleValueCondition"/>
/// </summary>
/// <typeparam name="T">what the operands are, before they are stringified for transport</typeparam>
internal interface IMultipleValueCondition<T> : IMultipleValueCondition, IValueContainer<ConditionValue<T>>
{ }
