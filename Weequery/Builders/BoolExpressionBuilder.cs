using System.Linq.Expressions;

namespace Weequery.Builders;

/// <summary>
/// Builds expressions for bool and bool? bindings.
/// </summary>
/// <remarks>
/// Only the meaningful operators are accepted. The ordering operators and the between
/// family are refused rather than passed through: a database will happily order a boolean column, but asking
/// whether one truth value is greater than another says nothing a caller could have meant, and letting it through
/// would fail later with a framework error about Boolean having no comparison operator.
/// </remarks>
internal class BoolExpressionBuilder : ExpressionBuilderBase<bool>
{
    /// <summary>
    /// The operators a bool binding accepts. Everything else is rejected up front, so the error names the operator
    /// and the type rather than surfacing from deep inside expression construction.
    /// </summary>
    private static bool IsSupported(Operator op)
    {
        switch (op)
        {
            case Operator.IsNull:
            case Operator.IsNotNull:
            case Operator.Equals:
            case Operator.NotEqual:
            case Operator.IsIn:
            case Operator.IsNotIn:
                return true;

            default:
                return false;
        }
    }

    public override Expression<Func<TClass, bool>> BuildTypedExpressionFromTypedCondition<TClass>(Binding<TClass> binding, TypedCondition<bool> condition)
    {
        WeequeryException.ThrowIfNull(binding);
        WeequeryException.ThrowIfNull(condition);

        if (!IsSupported(condition.Operator))
        {
            throw new WeequeryException($"Operator {condition.Operator} is unsupported for the bool binding '{binding.PropertyPath}', only null tests, equality and the IsIn family apply to a truth value");
        }

        // The shared implementation already handles both the plain and the Nullable<> forms of every operator
        // above, including the HasValue guard a bool? needs, so there is nothing bool-specific left to do
        var common = ExpressionBuilderFunctions.BuildCommonValueExpression(binding, condition);
        if (common is not null) { return common; }

        throw new WeequeryException($"Operator {condition.Operator} is unsupported for the bool binding '{binding.PropertyPath}'");
    }

    public override Expression<Func<TClass, bool>> BuildTypedExpressionFromStringifiedCondition<TClass>(Binding<TClass> binding, TypedCondition<string> condition)
    {
        WeequeryException.ThrowIfNull(binding);
        WeequeryException.ThrowIfNull(condition);

        if (binding.UnwrappedPropertyType != typeof(bool))
        {
            throw new WeequeryException($"Binding for {binding.PropertyPath} is not a {typeof(bool).Name}");
        }

        return BuildTypedExpressionFromTypedCondition(binding, condition.Transform(text => (bool)ValueFormat.Parse(typeof(bool), text)));
    }
}