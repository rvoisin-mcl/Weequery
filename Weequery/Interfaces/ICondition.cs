namespace Weequery.Interfaces;

/// <summary>
/// Base condition interface
/// </summary>
public interface ICondition
{
    /// <summary>
    /// What operation should be performed
    /// </summary>
    Operator Operator { get; }

    /// <summary>
    /// Should return a Packed Condition 
    /// </summary>
    /// <returns></returns>
    PackedCondition Pack();
}