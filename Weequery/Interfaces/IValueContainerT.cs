namespace Weequery.Interfaces;

/// <summary>
/// Values for a condition, separated out so <see cref="PackedCondition"/> can carry them
/// without being one
/// </summary>
/// <typeparam name="T">what the values are, before they are stringified for transport</typeparam>
public interface IValueContainer<T>
{
    /// <summary>
    /// Values to be used to test the condition
    /// </summary>
    List<T> Values { get; }
}
