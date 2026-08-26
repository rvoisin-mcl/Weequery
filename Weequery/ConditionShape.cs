namespace Weequery;

/// <summary>
/// How many operands a comparison holds, which is what decides the type that represents it. One per operand count
/// the operators take, so the shape and the operator agree by construction rather than by a check at the point
/// the condition is used.
/// </summary>
/// <remarks>
/// <see cref="ConditionFunctions.GetShapeForOperation"/> maps an operator to its shape, and answers null for the
/// three that combine conditions rather than testing a property.
/// </remarks>
public enum ConditionShape
{
    /// <summary>No operand at all: the operator asks about the property itself. <see cref="NoValueCondition"/></summary>
    NoValue,

    /// <summary>One operand, which is most of the comparisons. <see cref="OneValueCondition{T}"/></summary>
    OneValue,

    /// <summary>Two operands, which is the range family. <see cref="TwoValueCondition{T}"/></summary>
    TwoValue,

    /// <summary>A list of operands, which is the membership family. <see cref="MultipleValueCondition{T}"/></summary>
    MultipleValue,
}
