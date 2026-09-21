using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Weequery.Bindings;

namespace Weequery.Builders;

internal class ObjectExpressionBuilder : ExpressionBuilderBase<object>
{
    [RequiresDynamicCode(AotMessages.RuntimeGenerics)]
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    protected override Expression<Func<TClass, bool>> Build<TClass>(Binding<TClass> binding, TypedCondition<object> condition)
    {
        WeequeryException.ThrowIfNull(binding);
        WeequeryException.ThrowIfNull(condition);

        Expression? expression = null;

        switch (condition.Operator)
        {
            case Operator.IsNull:
                expression = Expression.Equal(binding.Accessor, Expression.Constant(null, typeof(object)));
                break;

            case Operator.IsNotNull:
                expression = Expression.NotEqual(binding.Accessor, Expression.Constant(null, typeof(object)));
                break;

            default:
                throw new WeequeryException(WeequeryError.OperatorUnsupported, $"Operator {condition.Operator} is unsupported for the {typeof(object).Name} binding '{binding.PropertyPath}', only the null tests apply to it");
        }

        return Expression.Lambda<Func<TClass, bool>>(expression, binding.Parameter);
    }

    [RequiresDynamicCode(AotMessages.RuntimeGenerics)]
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    public override Expression<Func<TClass, bool>> BuildTypedExpressionFromStringifiedCondition<TClass>(Binding<TClass> binding, TypedCondition<string> condition)
    {
        WeequeryException.ThrowIfNull(binding);
        WeequeryException.ThrowIfNull(condition);

        if ((binding.UnwrappedPropertyType != typeof(object)))
        {
            throw new WeequeryException(WeequeryError.BindingInvalid, $"Binding for {binding.PropertyPath} is not a {typeof(object).Name}");
        }

        return BuildTypedExpressionFromTypedCondition(binding, condition.Transform((string x) => (object)x));
    }
}