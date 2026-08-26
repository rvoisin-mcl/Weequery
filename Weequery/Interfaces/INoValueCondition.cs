namespace Weequery.Interfaces;

/// <summary>
/// A condition whose operator asks about the bound property itself rather than comparing it against anything, so
/// <see cref="Operator.IsNull"/> and <see cref="Operator.IsNotNull"/>.
/// </summary>
/// <remarks>
/// Not generic, because there is nothing to hold: the question is about the property, and the answer does not
/// depend on what type it is. <see cref="NoValueCondition"/> is the implementation.
/// </remarks>
public interface INoValueCondition : IBoundCondition
{ }
