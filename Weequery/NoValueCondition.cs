using Weequery.Interfaces;

namespace Weequery;

/// <summary>
/// <inheritdoc cref="INoValueCondition"/>
/// </summary>
/// <remarks>
/// Holds nothing but the field and which of the operator it is
/// </remarks>
public class NoValueCondition : BoundCondition, INoValueCondition
{
    /// <summary>
    /// ctor
    /// </summary>
    /// <param name="op"><see cref="Operator.IsNull"/> or <see cref="Operator.IsNotNull"/></param>
    /// <param name="field">the binding key to test</param>
    /// <param name="index">[OPT] which element of the collection to test, see <see cref="IBound.Index"/></param>
    /// <exception cref="WeequeryException">the field is missing, or the operator takes a value</exception>
    public NoValueCondition(Operator op, string field, string? index = null)
        : base(op, field, ConditionShape.NoValue, index)
    {
    }

    /// <summary>
    /// Nothing to compare against, so nothing to stringify
    /// </summary>
    /// <returns></returns>
    public override List<ConditionValue<string>> StringifyOperands()
    {
        return [];
    }
}
