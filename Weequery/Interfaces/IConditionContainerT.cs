namespace Weequery.Interfaces;

/// <summary>
/// The children of an <see cref="IConjunctionCondition"/> or an <see cref="INotCondition"/>, separated out so
/// <see cref="PackedCondition"/> can carry them without being one
/// </summary>
/// <typeparam name="T">what the children are: conditions in a tree, packed conditions in a packed one</typeparam>
public interface IConditionContainer<T> where T : ICondition
{
    /// <summary>
    /// Child conditions to be joined
    /// </summary>
    List<T> Conditions { get; }
}
