using System.Linq.Expressions;

namespace Weequery.Builders;

internal class ValueExpressionBuilder<T> : ExpressionBuilderBase<T> where T : struct
{
    public override Expression<Func<TClass, bool>> BuildTypedExpressionFromTypedCondition<TClass>(Binding<TClass> binding, TypedCondition<T> condition)
    {
        WeequeryException.ThrowIfNull(binding);
        WeequeryException.ThrowIfNull(condition);

        var commonExp = ExpressionBuilderFunctions.BuildCommonValueExpression(binding, condition);
        if (commonExp is not null) { return commonExp; }

        throw new WeequeryException($"Operator {condition.Operator} is unsupported for the {typeof(T).Name} binding '{binding.PropertyPath}'");
    }

    /// <summary>
    /// Given a string-ly typed condition, parse into the binding type
    /// </summary>
    /// <typeparam name="TClass"></typeparam>
    /// <param name="binding"></param>
    /// <param name="condition"></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException"></exception>
    public override Expression<Func<TClass, bool>> BuildTypedExpressionFromStringifiedCondition<TClass>(Binding<TClass> binding, TypedCondition<string> condition)
    {
        WeequeryException.ThrowIfNull(binding);
        WeequeryException.ThrowIfNull(condition);

        // Parsing will use invariant culture, matching .Stringify so round-tripping can work across machines
        Func<string, T> parseFunc = (x) => { return (T)ValueFormat.Parse(binding.UnwrappedPropertyType, x); };

        TypedCondition<T> typed;

        try
        {
            typed = condition.Transform(parseFunc);
        }
        catch (WeequeryException ex)
        {
            // Add some additional detail to the the bubbled exception
            throw new WeequeryException($"{ex.Message}, for field '{condition.Field}'", ex);
        }
        catch (Exception ex)
        {
            throw new WeequeryException($"Failed to parse a {typeof(T).Name} for field '{condition.Field}': {ex.Message}", ex);
        }

        return BuildTypedExpressionFromTypedCondition(binding, typed);
    }
}