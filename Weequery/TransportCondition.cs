using System.Text.Json.Serialization;
using Weequery.Interfaces;

namespace Weequery;

/// <summary>
/// Provide a container that can hold either a packed condition or a query string
/// </summary>
public class TransportCondition
{
    /// <summary>
    /// The condition as an object graph, if it was sent that way
    /// </summary>
    public PackedCondition? Condition { get; set; }

    /// <summary>
    /// The condition as a query string, if it was sent that way
    /// </summary>
    public string? Query { get; set; }

    /// <summary>
    /// For deserialization
    /// </summary>
    protected TransportCondition()
    { }

    /// <summary>
    /// Carry a condition, packed on the way in
    /// </summary>
    /// <param name="condition"></param>
    public TransportCondition(ICondition condition)
    {
        Condition = condition.Pack() as PackedCondition;
        Query = null;
    }

    /// <summary>
    /// Carry a condition that is already packed
    /// </summary>
    /// <param name="condition"></param>
    public TransportCondition(PackedCondition condition)
    {
        Condition = condition;
        Query = null;
    }

    /// <summary>
    /// Carry a condition as a query string, to be parsed by <see cref="Unpack"/>
    /// </summary>
    /// <param name="query"></param>
    public TransportCondition(string? query)
    {
        Condition = null;
        Query = query;
    }

    /// <summary>
    /// For deserialization, where a payload may carry either form
    /// </summary>
    /// <param name="condition"></param>
    /// <param name="query"></param>
    [JsonConstructor]
    protected TransportCondition(PackedCondition? condition, string? query)
    {
        Condition = condition;
        Query = query;
    }

    /// <summary>
    /// Attempt to unpack into to a condition, if both .Condition and .Query are present, it will prefer .Condition
    /// </summary>
    /// <returns></returns>
    public ICondition? Unpack()
    {
        return (Condition is not null) ? Condition.Unpack() : (Query is not null) ? ConditionFunctions.ParseQuery(Query) : null;
    }
}
