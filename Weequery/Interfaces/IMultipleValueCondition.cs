namespace Weequery.Interfaces;

/// <summary>
/// A condition that compares the bound property against a list of operands, which is the membership family:
/// <see cref="Operator.IsIn"/> and <see cref="Operator.IsNotIn"/>. The list may be empty, and both operators say
/// what that means.
/// </summary>
/// <remarks>
/// Non-generic, for typing and for the walks that only want the operands as text. Implementations should derive
/// from <see cref="IMultipleValueCondition{T}"/>; <see cref="MultipleValueCondition{T}"/> is the one here.
/// </remarks>
public interface IMultipleValueCondition : IBoundCondition
{ }
