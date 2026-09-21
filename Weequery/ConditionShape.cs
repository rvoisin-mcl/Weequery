namespace Weequery;

/// <summary>
/// How many operands a comparison holds, which is what dictates the type that represents it
/// </summary>
/// <remarks>
/// <see cref="ConditionFunctions.GetShapeForOperation"/> maps an operator to its shape
/// </remarks>
public enum ConditionShape
{
    /// <summary>No value, the operator asks about the property itself. <see cref="NoValueCondition"/></summary>
    NoValue,

    /// <summary>One value<see cref="OneValueCondition{T}"/></summary>
    OneValue,

    /// <summary>Two values<see cref="TwoValueCondition{T}"/></summary>
    TwoValue,

    /// <summary>A list of values<see cref="MultipleValueCondition{T}"/></summary>
    MultipleValue,
}
