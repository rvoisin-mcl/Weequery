namespace Weequery.Interfaces;

/// <summary>
/// Intended to be paired with <see cref="ICondition"/>, indicates that the condition requires a bound property
/// </summary>
public interface IBound
{
    /// <summary>
    /// Name of bound property
    /// </summary>
    string Field { get; }
}
