using Weequery.Interfaces;
using Weequery.Parsing;

namespace Weequery;

/// <summary>
/// A test over the elements of a bound collection: <see cref="Operator.Any"/>, <see cref="Operator.All"/> or
/// <see cref="Operator.None"/> of them satisfying the condition it holds.
/// </summary>
/// <remarks>
/// <para>
/// The only condition that is both bound and a container. It names a collection, as a comparison names a
/// property, and it holds one condition, as a negation does; what it does not hold is operands, since the test
/// is the condition inside rather than a value.
/// </para>
/// <code>
/// Assignments Any (LairID = 5 AND IsPrimary = true)
/// </code>
/// <para>
/// The point of the inner condition being one condition rather than several tests over the collection is that it
/// is scoped to <b>one element</b>. "Any assignment that is both to lair 5 and primary" is what this asks;
/// "some assignment is to lair 5, and some assignment is primary" is a different and weaker question, and it is
/// what you get from two separate quantifiers ANDed together.
/// </para>
/// <para>
/// The fields inside resolve against the collection's own allow-list rather than the entity's, see
/// <see cref="CollectionBindingSet{TElement}"/>. Nothing is reachable inside a collection until it is bound there,
/// which is the same rule the outer bindings follow.
/// </para>
/// <para>
/// <b>It is never unknown.</b> Every other bound condition can be defeated by a null, and this one cannot: a
/// collection either holds an element that matches or it does not. Empty and missing are the same answer, so Any
/// is false of both and All and None are true of both. See the remarks on <see cref="Operator.Any"/>.
/// </para>
/// </remarks>
public class QuantifiedCondition : ICondition, IBound, IConditionContainer<ICondition>
{
    /// <summary>Which quantifier this is</summary>
    public Operator Operator { get; init; }

    /// <summary>The key the collection was bound under, matched without regard to case</summary>
    public string Field { get; init; }

    /// <summary>
    /// The condition every element is put to, one at a time. Held as a list to match every other container, and
    /// it holds exactly one.
    /// </summary>
    public List<ICondition> Conditions { get; init; }

    /// <summary>
    /// The condition scoped to the element, which is what this quantifies over
    /// </summary>
    public ICondition Condition { get { return Conditions[0]; } }

    /// <summary>
    /// ctor
    /// </summary>
    /// <param name="op">must be <see cref="Operator.Any"/>, <see cref="Operator.All"/> or <see cref="Operator.None"/></param>
    /// <param name="field">the key the collection was bound under</param>
    /// <param name="condition">the test one element has to satisfy</param>
    /// <exception cref="WeequeryException">the operator is not a quantifier, or either argument is missing</exception>
    public QuantifiedCondition(Operator op, string field, ICondition condition)
    {
        WeequeryException.ThrowIfNullOrEmpty(field);
        WeequeryException.ThrowIfNull(condition);

        if (!IsQuantifier(op))
        {
            throw new WeequeryException(WeequeryError.OperatorInvalid, $"Operator '{op}' on field '{field}' is not a quantifier, so it cannot be represented by {nameof(QuantifiedCondition)}");
        }

        Operator = op;
        Field = field;
        Conditions = [condition];
    }

    /// <summary>
    /// If an operator quantifies over a collection rather than testing a property or combining conditions
    /// </summary>
    /// <param name="op"></param>
    /// <returns></returns>
    public static bool IsQuantifier(Operator op)
    {
        return (op == Operator.Any) || (op == Operator.All) || (op == Operator.None);
    }

    /// <summary>
    /// Packs into the shape the wire already had: an operator, a field and one child. Nothing about
    /// <see cref="PackedCondition"/> had to change to carry this.
    /// </summary>
    /// <returns></returns>
    /// <exception cref="WeequeryException">the tree nests deeper than <see cref="ConditionNesting.MaxDepth"/></exception>
    public PackedCondition Pack()
    {
        return new PackedCondition(Operator, Field, [], [Condition.Pack()]);
    }

    /// <summary>
    /// Renders in the query language, see <see cref="ConditionFunctions.ToQuery"/>
    /// </summary>
    /// <returns></returns>
    public override string ToString()
    {
        return QueryWriter.Describe(this);
    }
}
