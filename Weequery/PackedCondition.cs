using System.Text.Json.Serialization;
using Weequery.Interfaces;

namespace Weequery;

/// <summary>
/// A condition in its serializable shape: one type carrying whatever any condition needs, so that a tree can be
/// written as JSON and read back. <see cref="PackedCondition"/> is the implementation.
/// </summary>
public class PackedCondition : ICondition, IBound, IValueContainer<ConditionValue<string>>, IConditionContainer<PackedCondition>
{
    /// <summary>
    /// What this condition does, which decides which of the members below carry anything
    /// </summary>
    public Operator Operator { get; set; }

    /// <summary>
    /// The binding key this tests, for a comparison. Empty for a conjunction or a negation.
    /// </summary>
    public string Field { get; set; } = "";

    /// <summary>
    /// The operands to test against, in order. Empty for a conjunction, a negation, or an operator that takes no
    /// value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every operand is text, whatever the bound property holds: a packed condition carries meaning rather than
    /// types, and the expression builder reads the text against the property's type when the query is built.
    /// </para>
    /// <para>
    /// Each also carries what it is, see <see cref="ConditionValue{T}"/>: a value to compare against, or the key
    /// of another bound property to compare against. That travels with the operand rather than beside the list,
    /// so there is nowhere for a key to arrive as bare text and be compared against as though it were one, which
    /// for a string field would be a different question answered without complaint.
    /// </para>
    /// <para>
    /// In the JSON an operand that is a value is written as the value alone, and only one naming a property
    /// carries a source, so a condition comparing against text serializes to what it always did:
    /// <c>"Values": [ "8000", { "Source": 1, "Value": "Ceiling" } ]</c>. See
    /// <see cref="ConditionValueConverter"/>; the two are still told apart by shape rather than by anything read
    /// out of the text.
    /// </para>
    /// </remarks>
    public List<ConditionValue<string>> Values { get; set; } = [];

    /// <summary>
    /// The children, for a conjunction or a negation. Empty for a comparison.
    /// </summary>
    public List<PackedCondition> Conditions { get; set; } = [];

    /// <summary>
    /// For deserialization. Marked so System.Text.Json uses this rather than trying to choose between the
    /// parameterized constructors below, which it cannot do and which makes deserializing throw.
    /// <para>
    /// Deliberately the parameterless one: the property initializers above run first, so a payload that leaves a
    /// member out keeps the non-null default rather than being handed a null for it.
    /// </para>
    /// </summary>
    [JsonConstructor]
    private PackedCondition() // serialization support
    { }

    /// <summary>
    /// ctor, taking every member. Whichever the operator does not use should be empty rather than null.
    /// </summary>
    /// <param name="operator"></param>
    /// <param name="field"></param>
    /// <param name="values"></param>
    /// <param name="conditions"></param>
    public PackedCondition(Operator @operator, string field, List<ConditionValue<string>> values, List<PackedCondition> conditions)
    {
        Operator = @operator;
        Field = field;
        Values = values;
        Conditions = conditions;
    }

    /// <summary>
    /// Pack a comparison, with its operands stringified on the way in
    /// </summary>
    /// <param name="condition"></param>
    public PackedCondition(IBoundCondition condition)
    {
        WeequeryException.ThrowIfNull(condition);

        Operator = condition.Operator;
        Field = condition.Field;
        Values = [.. condition.StringifyOperands()]; // copy, don't keep
    }

    /// <summary>
    /// Pack a conjunction, and its children with it
    /// </summary>
    /// <param name="conjunction"></param>
    /// <exception cref="WeequeryException">the tree nests deeper than <see cref="ConditionNesting.MaxDepth"/></exception>
    public PackedCondition(IConjunctionCondition conjunction)
    {
        Operator = conjunction.Operator;
        Conditions = PackChildren(conjunction.Conditions);
    }

    /// <summary>
    /// Pack a negation, and the condition it negates with it
    /// </summary>
    /// <param name="condition"></param>
    /// <exception cref="WeequeryException">the tree nests deeper than <see cref="ConditionNesting.MaxDepth"/></exception>
    public PackedCondition(INotCondition condition)
    {
        Operator = condition.Operator;
        Conditions = PackChildren(condition.Conditions);
    }

    /// <summary>
    /// How deep the pack running on this thread has gone. Packing recurses through
    /// <see cref="ICondition.Pack"/>, whose signature belongs to the caller's own conditions, so the depth cannot
    /// be handed down as an argument the way the other walks hand it down. It is held for the thread instead and
    /// unwound however <see cref="PackChildren"/> returns.
    /// </summary>
    [ThreadStatic]
    private static int PackDepth;

    /// <summary>
    /// Pack the children of a container one level down, refusing to go past
    /// <see cref="ConditionNesting.MaxDepth"/>: packing is recursive, so a tree deep enough would otherwise
    /// overflow the stack.
    /// </summary>
    /// <param name="conditions"></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">the tree nests deeper than the limit</exception>
    private static List<PackedCondition> PackChildren(List<ICondition> conditions)
    {
        PackDepth = ConditionNesting.Descend(PackDepth);

        try
        {
            return conditions.Select(condition => condition.Pack()).ToList();
        }
        finally
        {
            PackDepth--;
        }
    }

    /// <summary>
    /// Already packed, so this one is itself
    /// </summary>
    /// <returns></returns>
    public PackedCondition Pack()
    {
        return this;
    }

    /// <summary>
    /// Renders in the query language, by way of <see cref="Unpack()"/>. See <see cref="ConditionFunctions.ToQuery"/>
    /// for the round-trippable form.
    /// </summary>
    /// <returns></returns>
    public override string ToString()
    {
        return QueryWriter.Describe(this);
    }

    /// <summary>
    /// Create a properly typed Condition instance for this
    /// </summary>
    /// <returns></returns>
    /// <exception cref="WeequeryException">
    /// the operator is unknown, a member the operator needs is missing, or the tree nests deeper than
    /// <see cref="ConditionNesting.MaxDepth"/>
    /// </exception>
    public ICondition Unpack()
    {
        return Unpack(0);
    }

    /// <summary>
    /// Unpack one level of the tree. A packed condition usually arrives from a caller, so the depth is carried
    /// down and checked, see <see cref="ConditionNesting"/>.
    /// </summary>
    /// <param name="depth">levels of nesting already stepped into on the way here</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException"></exception>
    private ICondition Unpack(int depth)
    {
        switch (Operator)
        {
            case Operator.Or:
            case Operator.And:
                WeequeryException.ThrowIfNull(Conditions);
                var nestedConjunction = ConditionNesting.Descend(depth);
                return new ConjunctionCondition(Operator, Conditions.Where(condition => (condition != null)).Select(condition => condition.Unpack(nestedConjunction)).ToList());

            case Operator.Not:
                WeequeryException.ThrowIfNull(Conditions);
                WeequeryException.ThrowIfNull(Conditions.FirstOrDefault());
                return new NotCondition(Operator, Conditions.First().Unpack(ConditionNesting.Descend(depth)));

            default:
                return UnpackComparison();
        }
    }

    /// <summary>
    /// Unpack a comparison, into whichever of the four shapes its operator calls for. Every value comes back as
    /// text: a packed condition carries meaning rather than types, and the expression builder reads the text
    /// against the bound property's type when the query is built.
    /// </summary>
    /// <returns></returns>
    /// <exception cref="WeequeryException">
    /// the operator is unknown, the field is missing, or there are the wrong number of operands for it
    /// </exception>
    private ICondition UnpackComparison()
    {
        WeequeryException.ThrowIfNull(Field);
        WeequeryException.ThrowIfNull(Values);

        // Each operand already says whether it is a value or the key of a property, so there is nothing to work
        // out here: the condition this builds compares against whatever the sender said it was comparing against.
        // A missing operand is refused by the condition itself, naming which of them it was.
        return ConditionFunctions.BuildComparison(Operator, Field, Values);
    }
}