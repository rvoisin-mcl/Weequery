namespace Weequery.Interfaces;

/// <summary>
/// implementations should derive from this
/// </summary>
public interface IConjunctionCondition : ICondition, IConditionContainer<ICondition>
{ }
