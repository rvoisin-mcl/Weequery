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
    /// Which fields to read back, as a comma separated list, where the caller asked for some of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null where nothing was asked, which is nearly every payload ever sent, so one written before any of this
    /// existed is the payload it always was. Read with <see cref="UnpackProjection"/> and handed to
    /// <see cref="Inquiry{T}.ApplyProjection(Projection?)"/>.
    /// </para>
    /// <para>
    /// A string rather than a list because that is what it is on the way in: the caller writes
    /// <c>"Name, Pay"</c>, the same field spelling a condition and a sort use, and the same allow-list decides
    /// what is legal, see <see cref="Projection"/>.
    /// </para>
    /// </remarks>
    public string? Projection { get; set; }

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
    /// <param name="style">
    /// how strictly to read the query half, where that is the half that arrived. <see cref="QueryStyle.Native"/>
    /// accepts one spelling per operator; null, the default, accepts every spelling. It has no bearing on the
    /// packed half, which carries operators as values rather than as text. See
    /// <see cref="ConditionFunctions.ParseQuery"/>
    /// </param>
    /// <returns></returns>
    public ICondition? Unpack(QueryStyle? style = null)
    {
        return (Condition is not null) ? Condition.Unpack() : (Query is not null) ? ConditionFunctions.ParseQuery(Query, style) : null;
    }

    /// <summary>
    /// Read the projection half, where the payload carried one.
    /// </summary>
    /// <remarks>
    /// Takes no style: a projection is a list of field names and holds no operators, so there is no spelling to
    /// be strict about. Safe to hand to <see cref="Inquiry{T}.ApplyProjection(Projection?)"/> whatever arrived,
    /// since nothing named gives <see cref="Weequery.Projection.None"/> and that is a no-op.
    /// </remarks>
    /// <returns><see cref="Weequery.Projection.None"/> where nothing was asked; never null</returns>
    /// <exception cref="WeequeryException">the list is malformed</exception>
    public Projection UnpackProjection()
    {
        return Weequery.Projection.Parse(Projection);
    }
}
