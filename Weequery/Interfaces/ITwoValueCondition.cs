namespace Weequery.Interfaces;

/// <summary>
/// A condition that compares the bound property against a pair of operands, which is the range family:
/// <see cref="Operator.IsBetween"/> and <see cref="Operator.IsNotBetween"/>.
/// </summary>
/// <remarks>
/// Non-generic, for typing and for the walks that only want the operands as text. Implementations should derive
/// from <see cref="ITwoValueCondition{T}"/>; <see cref="TwoValueCondition{T}"/> is the one here.
/// </remarks>
public interface ITwoValueCondition : IBoundCondition
{ }
