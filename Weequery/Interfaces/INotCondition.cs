namespace Weequery.Interfaces;

/// <summary>
/// implementations should derive from this
/// </summary>
public interface INotCondition : ICondition, IConditionContainer<ICondition>
{ }