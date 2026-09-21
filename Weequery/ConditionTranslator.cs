using Weequery.Interfaces;

namespace Weequery;

/// <summary>
/// The walk over a condition tree, for anything turning one into something other than an expression tree.
/// </summary>
/// <remarks>
/// <para>
/// Every translation of a condition has the same skeleton and differs only in what it writes: unwrap a packed
/// condition, dispatch on the five shapes, bound the recursion, and refuse what is not one of them. Written
/// twice it is written twice wrongly, and the parts that are easy to get subtly wrong — the depth bound, a
/// negation with nothing to negate, whether a conjunction over no operands matches everything or nothing — are
/// exactly the parts nobody thinks to check the second time.
/// </para>
/// <para>
/// So the shape lives here and a translator writes only its own syntax:
/// <code>
/// internal sealed class MyTranslator(MyFields fields) : ConditionTranslator&lt;string, MyScope&gt;
/// {
///     protected override string Dialect =&gt; "my dialect";
///
///     protected override string Compare(IBoundCondition condition, MyScope scope) =&gt; ...;
///     protected override string Combine(Operator conjunction, IReadOnlyList&lt;string&gt; parts) =&gt; ...;
///     protected override string Negate(string operand) =&gt; $"not {operand}";
///     protected override string Quantify(QuantifiedCondition condition, MyScope scope, int depth) =&gt; ...;
/// }
/// </code>
/// </para>
/// <para>
/// <b>What is deliberately not here is the null handling.</b> Weequery's operators carry a guard and most other
/// languages do not, but which of theirs already agree and which need one put back is a fact about that
/// language rather than about conditions. Each translator settles it for itself, and says so where it does.
/// </para>
/// </remarks>
/// <typeparam name="TResult">what a condition becomes: a string, a JSON object, whatever the target reads</typeparam>
/// <typeparam name="TScope">
/// what a translator needs to know about where it is, which for most is the collection currently being
/// quantified over. Use a nullable reference or a nullable tuple where the top level has no scope at all.
/// </typeparam>
public abstract class ConditionTranslator<TResult, TScope>
{
    /// <summary>
    /// The name of what is being written, for the message when a condition holds something it has no answer for
    /// </summary>
    protected abstract string Dialect { get; }

    /// <summary>
    /// One comparison: a bound field, an operator and its operands.
    /// </summary>
    /// <param name="condition"></param>
    /// <param name="scope">where in the tree this is, see <typeparamref name="TScope"/></param>
    /// <returns></returns>
    protected abstract TResult Compare(IBoundCondition condition, TScope scope);

    /// <summary>
    /// The operands of an AND or an OR, already translated, joined the way the target joins them.
    /// </summary>
    /// <remarks>
    /// <b>An empty list is a real case and means something.</b> AND over no operands matches everything and OR
    /// over none matches nothing, which are the identities of the two and what an empty conjunction is. Say both
    /// rather than assuming a caller cannot build one; the parser will not, and a hand built tree will.
    /// </remarks>
    /// <param name="conjunction"><see cref="Operator.And"/> or <see cref="Operator.Or"/></param>
    /// <param name="parts">the translated operands, in the order they were held</param>
    /// <returns></returns>
    protected abstract TResult Combine(Operator conjunction, IReadOnlyList<TResult> parts);

    /// <summary>
    /// A negation of something already translated.
    /// </summary>
    /// <remarks>
    /// Whatever this writes should <b>not</b> put a null guard back. Negating a condition negates its guard with
    /// it, which is why <c>NOT (Alias = 'Ghost')</c> is meant to return the rows with no alias where
    /// <c>Alias &lt;&gt; 'Ghost'</c> is not. A guard belongs on the negative operators, in <see cref="Compare"/>.
    /// </remarks>
    /// <param name="operand"></param>
    /// <returns></returns>
    protected abstract TResult Negate(TResult operand);

    /// <summary>
    /// A quantifier over a bound collection.
    /// </summary>
    /// <remarks>
    /// Left whole rather than split up, because the inner condition has to be translated in a scope the
    /// implementation decides: it is the one that knows what the collection is called in the target and what a
    /// field inside it looks like. Call <see cref="Translate"/> with the new scope and the depth handed in.
    /// </remarks>
    /// <param name="condition"></param>
    /// <param name="scope">the scope this quantifier appears in, which is usually the one to refuse nesting in</param>
    /// <param name="depth">levels already stepped into, to hand back to <see cref="Translate"/></param>
    /// <returns></returns>
    protected abstract TResult Quantify(QuantifiedCondition condition, TScope scope, int depth);

    /// <summary>
    /// Translate a condition.
    /// </summary>
    /// <param name="condition"></param>
    /// <param name="scope">where this is being translated, see <typeparamref name="TScope"/></param>
    /// <param name="depth">[OPT] levels already stepped into; leave it alone at the top</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">
    /// the condition holds something the target has no answer for, a negation has nothing to negate, or the tree
    /// nests deeper than <see cref="ConditionNesting.MaxDepth"/>
    /// </exception>
    protected TResult Translate(ICondition condition, TScope scope, int depth = 0)
    {
        WeequeryException.ThrowIfNull(condition);

        // A packed condition carries the same tree in a serializable shape, so translate what it unpacks to.
        // Not a level deeper: it is the same condition, spelled for the wire.
        if (condition is PackedCondition packed) { return Translate(packed.Unpack(), scope, depth); }

        // Before IBoundCondition, which it is not, and before the containers, which it half is: a quantifier
        // names a field the way a comparison does and holds a condition the way a container does
        if (condition is QuantifiedCondition quantified) { return Quantify(quantified, scope, depth); }

        if (condition is IBoundCondition comparison) { return Compare(comparison, scope); }

        if (condition is IConjunctionCondition conjunction)
        {
            if ((conjunction.Operator != Operator.And) && (conjunction.Operator != Operator.Or))
            {
                throw new WeequeryException($"Operator {conjunction.Operator} is invalid for {nameof(IConjunctionCondition)}");
            }

            var nested = ConditionNesting.Descend(depth);

            var parts = (from child in conjunction.Conditions select Translate(child, scope, nested)).ToList();

            return Combine(conjunction.Operator, parts);
        }

        if (condition is INotCondition negation)
        {
            var operand = negation.Conditions.FirstOrDefault()
                ?? throw new WeequeryException($"{nameof(Operator.Not)} has no condition to negate, so there is nothing to write as {Dialect}");

            return Negate(Translate(operand, scope, ConditionNesting.Descend(depth)));
        }

        throw new WeequeryException($"Condition type '{condition.GetType().Name}' has no representation in {Dialect}");
    }

    /// <summary>
    /// The one value an operator takes, refusing a condition holding the wrong number of them.
    /// </summary>
    /// <remarks>
    /// A condition off the wire has been checked, and one assembled by hand has not; the lists it holds are
    /// public either way. Here because every translator needs it and none of them should be reading
    /// <c>values[0]</c> and hoping.
    /// </remarks>
    /// <typeparam name="TValue"></typeparam>
    /// <param name="values"></param>
    /// <param name="condition">the condition they came from, so the message can name it</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">there is not exactly one</exception>
    protected static TValue Single<TValue>(IReadOnlyList<TValue> values, IBoundCondition condition)
    {
        return (values.Count == 1)
            ? values[0]
            : throw new WeequeryException($"Operator {condition.Operator} on field '{condition.Field}' needs one value but got {values.Count}");
    }
}
