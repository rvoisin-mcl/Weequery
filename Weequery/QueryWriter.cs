using Weequery.Interfaces;

namespace Weequery;

/// <summary>
/// Writes a condition back out in the query language, so that feeding the result to
/// <see cref="ConditionFunctions.ParseQuery"/> produces an equivalent condition.
/// <para>
/// Two entry points, differing only in how they handle a condition the language cannot express:
/// <see cref="Write"/> throws, because a caller asking for a round-trippable string needs to know it did not get
/// one, while <see cref="Describe"/> substitutes a readable placeholder, because it backs ToString and throwing
/// from ToString makes debugging worse. A condition nested past <see cref="ConditionNesting.MaxDepth"/> is handled
/// the same way round: refused for <see cref="Write"/>, and written as a placeholder for <see cref="Describe"/>.
/// </para>
/// <para>
/// The round trip preserves meaning, not types. Every value is written as text and comes back as a comparison
/// over string, which is the same thing <see cref="PackedCondition.Unpack()"/> does: the expression builder parses
/// the text against the bound property's type when the query is built.
/// </para>
/// </summary>
internal static class QueryWriter
{
    /// <summary>
    /// Write a condition as a query string that <see cref="ConditionFunctions.ParseQuery"/> can read back.
    /// </summary>
    /// <param name="condition"></param>
    /// <param name="style">which spelling to use for the operators that have two</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">
    /// the condition has no representation in the query language, or nests deeper than
    /// <see cref="ConditionNesting.MaxDepth"/>
    /// </exception>
    public static string Write(ICondition condition, QueryStyle style)
    {
        WeequeryException.ThrowIfNull(condition);

        return Render(condition, strict: true, style, 0);
    }

    /// <summary>
    /// Write a condition for display. Prefers the round-trippable form, but will not throw.
    /// </summary>
    /// <param name="condition"></param>
    /// <returns></returns>
    public static string Describe(ICondition condition)
    {
        return (condition is null) ? string.Empty : Render(condition, strict: false, QueryStyle.CSharp, 0);
    }

    /// <summary>
    /// What a condition nested past <see cref="ConditionNesting.MaxDepth"/> is written as when the caller cannot
    /// be thrown at. In the same shape as the other placeholders here, so it reads as one of them.
    /// </summary>
    private const string TooDeep = "<nested too deep>";

    /// <param name="condition"></param>
    /// <param name="strict">true to refuse what cannot be round tripped, false to write a placeholder for it</param>
    /// <param name="style">which spelling to use for the operators that have two</param>
    /// <param name="depth">levels of nesting stepped into on the way here, see <see cref="ConditionNesting"/></param>
    private static string Render(ICondition condition, bool strict, QueryStyle style, int depth)
    {
        // Writing recurses, so a tree deep enough would overflow the stack. Strict callers are told, since a
        // string they cannot parse back is no use to them, while ToString settles for saying where it stopped.
        if (ConditionNesting.IsTooDeep(depth))
        {
            if (strict) { throw ConditionNesting.TooDeep(); }

            return TooDeep;
        }

        // A PackedCondition carries the same tree in a serializable shape, so write what it unpacks to
        if (condition is PackedCondition packedCondition)
        {
            // Unpacking is depth limited in its own right, and throws where this must not, so a tree too deep to
            // unpack gets the placeholder rather than taking ToString down with it
            if (!strict)
            {
                try
                {
                    return Render(packedCondition.Unpack(), strict, style, depth);
                }
                catch (WeequeryException)
                {
                    return TooDeep;
                }
            }

            return Render(packedCondition.Unpack(), strict, style, depth);
        }

        if (condition is IBoundCondition boundCondition)
        {
            return RenderBoundCondition(boundCondition, style);
        }

        if (condition is IConjunctionCondition conjunction)
        {
            return RenderConjunction(conjunction, strict, style, depth);
        }

        if (condition is INotCondition notCondition)
        {
            if (notCondition.Conditions.Count == 0)
            {
                if (strict) { throw new WeequeryException($"{nameof(Operator.Not)} has no condition to negate, so it cannot be written as a query"); }

                return "!<nothing>";
            }

            // "!" can butt up against its operand, "NOT" needs a space to stay a separate word
            var not = ConditionFunctions.GetOperationString(Operator.Not, style);
            var gap = (style == QueryStyle.Sql) ? " " : string.Empty;

            return $"{not}{gap}{Render(notCondition.Conditions.First(), strict, style, depth + 1)}";
        }

        if (strict) { throw new WeequeryException($"Condition type '{condition.GetType().Name}' cannot be written as a query"); }

        return $"<{condition.GetType().Name}>";
    }

    private static string RenderConjunction(IConjunctionCondition conjunction, bool strict, QueryStyle style, int depth)
    {
        if ((conjunction.Operator != Operator.And) && (conjunction.Operator != Operator.Or))
        {
            throw new WeequeryException($"Operator {conjunction.Operator} is invalid for {nameof(IConjunctionCondition)}");
        }

        var separator = $" {ConditionFunctions.GetOperationString(conjunction.Operator, style)} ";

        if (conjunction.Conditions.Count == 0)
        {
            // The language has no way to say "match everything" or "match nothing" on its own. The parser can
            // never produce this, it only arrives from a hand built tree.
            if (strict)
            {
                throw new WeequeryException($"An empty {conjunction.Operator} condition has no representation in the query language, so it cannot be round tripped");
            }

            return $"(<empty {conjunction.Operator}>)";
        }

        return $"({string.Join(separator, from component in conjunction.Conditions select Render(component, strict, style, depth + 1))})";
    }

    private static string RenderBoundCondition(IBoundCondition condition, QueryStyle style)
    {
        // Values are quoted when they came from a string condition, so text that happens to look like a number or
        // a keyword survives, and left bare otherwise so dates and numbers stay readable. An operand that names a
        // property is never quoted either way, see Operand.
        var quoteValues = HoldsText(condition);
        var operands = from operand in condition.StringifyOperands() select Operand(operand, quoteValues);

        var field = Field(condition.Field);
        var op = ConditionFunctions.GetOperationString(condition.Operator, style);

        switch (ConditionFunctions.GetShapeForOperation(condition.Operator))
        {
            case ConditionShape.NoValue:
                return $"({field} {op})";

            case ConditionShape.OneValue:
                return $"({field} {op} {operands.First()})";

            // Both of the list shapes write their operands in parentheses, which is what the parser reads back
            case ConditionShape.TwoValue:
            case ConditionShape.MultipleValue:
                return $"({field} {op} ({string.Join(", ", operands)}))";

            default:
                throw new WeequeryException($"Operation '{condition.Operator}' cannot be represented by {nameof(IBoundCondition)}");
        }
    }

    /// <summary>
    /// Whether the condition's operands are already text, which is what decides the quoting above. A condition
    /// holding values of some other type has them formatted on the way out, and a number or a date reads better
    /// bare; one holding text has to be quoted, or text that spells a number would not come back as text.
    /// </summary>
    private static bool HoldsText(IBoundCondition condition)
    {
        return condition is IOneValueCondition<string> or ITwoValueCondition<string> or IMultipleValueCondition<string>;
    }

    /// <summary>
    /// Bracket quote a field whose name is a plain identifier or property path, and single quote anything else.
    /// A binding key can be any string the caller chose, including one with spaces or punctuation in it.
    /// </summary>
    internal static string Field(string field)
    {
        return QueryTokenizer.IsBareWord(field) ? $"[{field}]" : ValueFormat.Quote(field);
    }

    /// <summary>
    /// One thing on the right of a comparison: a property in the brackets that say so, which is how it is read
    /// back, or a value written as any other value is. Quoting a key would make it a value again.
    /// </summary>
    /// <param name="operand"></param>
    /// <param name="quote">whether a value is quoted, see <see cref="HoldsText"/></param>
    /// <returns></returns>
    private static string Operand(ConditionValue<string> operand, bool quote)
    {
        return operand.NamesProperty ? $"[{operand.Value}]" : Literal(operand.Value, quote);
    }

    /// <summary>
    /// Quote a value when it has to be quoted to survive tokenizing, leave it bare when it does not
    /// </summary>
    private static string Literal(string value, bool quote)
    {
        return (quote || (!QueryTokenizer.IsBareWord(value))) ? ValueFormat.Quote(value) : value;
    }
}
