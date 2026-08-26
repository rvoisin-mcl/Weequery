using System.Linq.Expressions;
using System.Reflection;

namespace Weequery.Builders;

/// <summary>
/// Builds expressions for string bindings.
/// </summary>
/// <remarks>
/// <para>
/// The single argument string overloads below are chosen on purpose, and the inconsistency between them is known:
/// string.StartsWith(string) and string.EndsWith(string) compare linguistically against the current culture,
/// while string.Contains(string) is ordinal. That only shows up when the expression is evaluated in memory; EF
/// Core turns all six into SQL, where the column's collation decides instead.
/// </para>
/// <para>
/// Do not "fix" this by switching to the StringComparison overloads. EF Core cannot translate those, so the
/// operators would stop working against a database, which is the primary use of this library. The behaviour is
/// documented for callers on <see cref="Operator"/>, and pinned by the characterization tests in
/// StringMatchingSemanticsTests.
/// </para>
/// </remarks>
internal class StringExpressionBuilder : ExpressionBuilderBase<string>
{

    public override Expression<Func<TClass, bool>> BuildTypedExpressionFromTypedCondition<TClass>(Binding<TClass> binding, TypedCondition<string> condition)
    {
        WeequeryException.ThrowIfNull(binding);
        WeequeryException.ThrowIfNull(condition);

        Expression? expression = null;
        switch (condition.Operator)
        {
            case Operator.StartsWith:
                expression = Call(binding, StringMethods.StartsWith, condition);
                break;

            case Operator.DoesNotStartWith:
                expression = Expression.Not(Call(binding, StringMethods.StartsWith, condition));
                break;

            case Operator.EndsWith:
                expression = Call(binding, StringMethods.EndsWith, condition);
                break;

            case Operator.DoesNotEndWith:
                expression = Expression.Not(Call(binding, StringMethods.EndsWith, condition));
                break;

            case Operator.Contains:
                expression = Call(binding, StringMethods.Contains, condition);
                break;

            case Operator.DoesNotContain:
                expression = Expression.Not(Call(binding, StringMethods.Contains, condition));
                break;

            case Operator.IsMatch:
                // Static rather than an instance call, which is the shape a provider translates. Where the match
                // runs here instead, RegexTimeout swaps this for the bounded overload. See Operator.IsMatch for
                // which providers can do anything with it at all.
                expression = Match(binding, condition);
                break;

            case Operator.DoesNotMatch:
                // Negated inside the guard rather than outside it, so a null still matches nothing, which is what
                // makes this the negative operator rather than a negation. See the remarks on Operator.
                expression = Expression.Not(Match(binding, condition));
                break;

            case Operator.LessThan:
                expression = Expression.LessThan(Compare(binding, condition.Values[0]), StringMethods.Zero);
                break;

            case Operator.LessThanOrEqual:
                expression = Expression.LessThanOrEqual(Compare(binding, condition.Values[0]), StringMethods.Zero);
                break;

            case Operator.GreaterThan:
                expression = Expression.GreaterThan(Compare(binding, condition.Values[0]), StringMethods.Zero);
                break;

            case Operator.GreaterThanOrEqual:
                expression = Expression.GreaterThanOrEqual(Compare(binding, condition.Values[0]), StringMethods.Zero);
                break;

            case Operator.IsBetween:
                expression = Between(binding, condition);
                break;

            case Operator.IsNotBetween:
                expression = Expression.Not(Between(binding, condition));
                break;

            default:
                // Everything a string shares with the value types: the null tests, equality and the IsIn family
                var common = ExpressionBuilderFunctions.BuildCommonValueExpression(binding, condition);
                if (common is not null) { return common; }

                throw new WeequeryException($"Operator {condition.Operator} is unsupported for the string binding '{binding.PropertyPath}'");
        }

        // Guard in front of the call, so a null is never dereferenced and never matches
        var guarded = binding.RequiresNullCheck ? Expression.AndAlso(binding.NotNullCheck, expression) : expression;

        return Expression.Lambda<Func<TClass, bool>>(guarded, binding.Parameter);
    }

    /// <summary>
    /// property.Method(value), on the value with any Nullable&lt;&gt; already stepped through
    /// </summary>
    private static Expression Call<TClass>(Binding<TClass> binding, MethodInfo method, TypedCondition<string> condition)
    {
        return Expression.Call(binding.UnwrappedAccessor, method, QueryValue.Of(condition.Values[0]));
    }

    /// <summary>
    /// Regex.IsMatch(property, pattern), on the value with any Nullable&lt;&gt; already stepped through
    /// </summary>
    private static Expression Match<TClass>(Binding<TClass> binding, TypedCondition<string> condition)
    {
        return Expression.Call(StringMethods.IsMatch, binding.UnwrappedAccessor, QueryValue.Of(condition.Values[0]));
    }

    /// <summary>
    /// string.Compare(property, value), which is negative, zero or positive as the property sorts before, with, or
    /// after the value
    /// </summary>
    private static Expression Compare<TClass>(Binding<TClass> binding, string value)
    {
        return Expression.Call(StringMethods.Compare, binding.UnwrappedAccessor, QueryValue.Of(value));
    }

    /// <summary>
    /// value >= low AND value &lt;= high, in the order the comparison gives, inclusive of both ends as the range is
    /// everywhere else
    /// </summary>
    private static Expression Between<TClass>(Binding<TClass> binding, TypedCondition<string> condition)
    {
        return Expression.AndAlso(
            Expression.GreaterThanOrEqual(Compare(binding, condition.Values[0]), StringMethods.Zero),
            Expression.LessThanOrEqual(Compare(binding, condition.Values[1]), StringMethods.Zero));
    }

    /// <summary>
    /// Verify that binding is a string, and pass thru
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

        if (binding.UnwrappedPropertyType != typeof(string))
        {
            throw new WeequeryException($"Binding for {binding.PropertyPath} is not a {typeof(string).Name}");
        }

        return BuildTypedExpressionFromTypedCondition(binding, condition);
    }
}
