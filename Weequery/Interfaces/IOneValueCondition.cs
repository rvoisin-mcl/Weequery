namespace Weequery.Interfaces;

/// <summary>
/// A condition that compares the bound property against a single operand: <see cref="Operator.Equals"/> and
/// <see cref="Operator.NotEqual"/>, the four ordering operators, and the six substring operators.
/// </summary>
/// <remarks>
/// Non-generic, for typing and for the walks that only want the operand as text. Implementations should derive
/// from <see cref="IOneValueCondition{T}"/>; <see cref="OneValueCondition{T}"/> is the one here.
/// </remarks>
public interface IOneValueCondition : IBoundCondition
{ }
