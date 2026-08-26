namespace Weequery.Interfaces;

/// <summary>
/// What every condition on a bound property shares: the property, the operator, and operands that can be read as
/// text however they are held.
/// </summary>
/// <remarks>
/// <para>
/// There are four such conditions, one per operand count the operators take:
/// <see cref="INoValueCondition"/>, <see cref="IOneValueCondition"/>, <see cref="ITwoValueCondition"/> and
/// <see cref="IMultipleValueCondition"/>. Each holds exactly what its operators need, so a condition that exists
/// is a condition with the right number of operands for what it does, and the count is settled by the type rather
/// than checked wherever one is used.
/// </para>
/// <para>
/// This is what everything that walks a condition without caring which of the four it is reads:
/// <see cref="ICondition.Pack"/>, the query writer and the expression builder all want the field, the operator,
/// and the operands as text.
/// </para>
/// </remarks>
public interface IBoundCondition : ICondition, IBound
{
    /// <summary>
    /// The operands in order, as text, each carrying whether it is a value or the key of another bound property
    /// to compare against. Empty for an operator that takes none.
    /// </summary>
    /// <returns></returns>
    List<ConditionValue<string>> StringifyOperands();

    /// <summary>
    /// The operands in order, as plain text. See <see cref="StringifyOperands"/> for which of them name a
    /// property rather than being a value.
    /// </summary>
    /// <returns></returns>
    List<string> StringifyValues();
}
