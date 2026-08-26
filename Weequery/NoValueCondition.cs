using Weequery.Interfaces;

namespace Weequery;

/// <summary>
/// <inheritdoc cref="INoValueCondition"/>
/// </summary>
/// <remarks>
/// Holds nothing but the field and which of the two tests it is, and is not generic, because what the property
/// holds does not change the question. See the remarks on <see cref="Operator"/> for what a null means to every
/// other operator.
/// </remarks>
public class NoValueCondition : BoundCondition, INoValueCondition
{
    /// <summary>
    /// ctor
    /// </summary>
    /// <param name="op"><see cref="Operator.IsNull"/> or <see cref="Operator.IsNotNull"/></param>
    /// <param name="field">the binding key to test</param>
    /// <exception cref="WeequeryException">the field is missing, or the operator takes a value</exception>
    public NoValueCondition(Operator op, string field)
        : base(op, field, ConditionShape.NoValue)
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
