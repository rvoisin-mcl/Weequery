using System.Diagnostics.CodeAnalysis;

namespace Weequery.Interfaces;

/// <summary>
/// Base condition interface
/// </summary>
public interface ICondition
{
    /// <summary>
    /// What operation should be performed
    /// </summary>
    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Named for the enum it returns, which is named for the query language, see Operator.")]
    Operator Operator { get; }

    /// <summary>
    /// Should return a Packed Condition 
    /// </summary>
    /// <returns></returns>
    PackedCondition Pack();
}