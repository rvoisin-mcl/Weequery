using System.Linq.Expressions;
using System.Reflection;

namespace Weequery.Builders;

internal static class ExpressionBuilderFunctions
{
    /// <summary>
    /// One reflection lookup per closed generic type, held by the runtime for the life of the process
    /// </summary>
    /// <typeparam name="TValue"></typeparam>
    private static class ListContains<TValue>
    {
        public static readonly MethodInfo Method = typeof(List<TValue>).GetMethod(nameof(List<TValue>.Contains), [typeof(TValue)])
            ?? throw new WeequeryException($"(Should be impossible) No List<{typeof(TValue).Name}>.Contains method"); // ex is to eat warning
    }

    /// <summary>
    /// Build 'values.Contains(property)' for the IsIn family.
    /// <para>
    /// A chain of ORed equality tests would mean the same thing, but providers recognise Contains and translate it
    /// to their native list membership form: 'IN (...)' on SQLite and SQL Server, and '= ANY (@p)' on PostgreSQL,
    /// which takes the whole list as a single parameter. That gives one SQL statement, and so one query plan,
    /// whatever the length of the list, where an OR chain gives a different statement for every length.
    /// </para>
    /// </summary>
    /// <typeparam name="TClass"></typeparam>
    /// <typeparam name="TProperty"></typeparam>
    /// <param name="binding"></param>
    /// <param name="values">must not be empty, callers short circuit that case</param>
    /// <returns></returns>
    private static Expression BuildContainsCheck<TClass, TProperty>(Binding<TClass> binding, List<TProperty> values)
    {
        // The list itself is parameterized the same way single values are, so it does not land in the SQL as literals
        return Expression.Call(QueryValue.Of(values), ListContains<TProperty>.Method, binding.PropertyIsWrappedByNullable ? binding.UnwrappedAccessor : binding.Accessor);
    }

    /// <summary>
    /// Build expressions for the common operations
    /// </summary>
    /// <remarks>
    /// Every operator here is the same shape: the binding's <see cref="Binding{TClass}.NotNullCheck"/> ANDed with a
    /// test on the value. That is what gives the answers a database gives, where a null satisfies nothing but
    /// IS NULL, including the negative operators: a null is not "not equal to 5", it is unknown, so it does not
    /// come back. IsNull is the one exception, being the negation of the guard.
    /// </remarks>
    /// <typeparam name="TClass"></typeparam>
    /// <typeparam name="TProperty"></typeparam>
    /// <param name="binding"></param>
    /// <param name="condition"></param>
    /// <returns>null if operation is unhandled</returns>
    internal static Expression<Func<TClass, bool>>? BuildCommonValueExpression<TClass, TProperty>(Binding<TClass> binding, TypedCondition<TProperty> condition)
    {
        WeequeryException.ThrowIfNull(binding);
        WeequeryException.ThrowIfNull(condition);

        // Values is public, ensure contents have not become invalid
        ConditionFunctions.ValidateValueCount(condition.Operator, condition.Field, condition.Values.Count);

        // The value, with any Nullable<> on the property itself already stepped through. Safe to use because
        // everything built from it is guarded by NotNullCheck below.
        var value = binding.UnwrappedAccessor;

        switch (condition.Operator)
        {
            case Operator.IsNull:
                if (!binding.AccessorIsNullable)
                {
                    throw new WeequeryException($"Operator {condition.Operator} is unsupported for Binding '{binding.PropertyPath}'");
                }
                return Lambda<TClass>(Expression.Not(binding.NotNullCheck), binding);

            case Operator.IsNotNull:
                if (!binding.AccessorIsNullable)
                {
                    throw new WeequeryException($"Operator {condition.Operator} is unsupported for Binding '{binding.PropertyPath}'");
                }
                return Lambda<TClass>(binding.NotNullCheck, binding);

            case Operator.Equals:
                return Guarded<TClass>(binding, Expression.Equal(value, QueryValue.Of(condition.Values[0])));

            case Operator.NotEqual:
                return Guarded<TClass>(binding, Expression.NotEqual(value, QueryValue.Of(condition.Values[0])));

            case Operator.LessThan:
                return Guarded<TClass>(binding, Ordered(binding, condition.Values[0], Expression.LessThan));

            case Operator.LessThanOrEqual:
                return Guarded<TClass>(binding, Ordered(binding, condition.Values[0], Expression.LessThanOrEqual));

            case Operator.GreaterThan:
                return Guarded<TClass>(binding, Ordered(binding, condition.Values[0], Expression.GreaterThan));

            case Operator.GreaterThanOrEqual:
                return Guarded<TClass>(binding, Ordered(binding, condition.Values[0], Expression.GreaterThanOrEqual));

            case Operator.IsBetween:
                return Guarded<TClass>(binding, Between(binding, condition));

            case Operator.IsNotBetween:
                return Guarded<TClass>(binding, Expression.Not(Between(binding, condition)));

            case Operator.IsIn:
                // No values means nothing to be in, so no row qualifies whatever the column holds
                return (condition.Values.Count == 0)
                    ? Lambda<TClass>(Expression.Constant(false), binding)
                    : Guarded<TClass>(binding, BuildContainsCheck(binding, condition.Values));

            case Operator.IsNotIn:
                // Nothing to be excluded by, so every row with a value qualifies
                return (condition.Values.Count == 0)
                    ? Guarded<TClass>(binding, Expression.Constant(true))
                    : Guarded<TClass>(binding, Expression.Not(BuildContainsCheck(binding, condition.Values)));

            default:
                return null;
        }
    }

    /// <summary>
    /// value >= low AND value &lt;= high
    /// </summary>
    private static Expression Between<TClass, TProperty>(Binding<TClass> binding, TypedCondition<TProperty> condition)
    {
        return Expression.AndAlso(
            Ordered(binding, condition.Values[0], Expression.GreaterThanOrEqual),
            Ordered(binding, condition.Values[1], Expression.LessThanOrEqual));
    }

    /// <summary>
    /// One ordering comparison, on operands that can be ordered.
    /// <para>
    /// An enum cannot, as far as the expression API is concerned: C# compares one by converting to the type it is
    /// based on, and it does that at compile time, so <see cref="Expression.LessThan(Expression, Expression)"/>
    /// reports that the operator is not defined for the type. Converting both sides here is the same thing the
    /// compiler does, and the underlying type is what the value is stored as, so a provider still sees a plain
    /// comparison against a parameter.
    /// </para>
    /// </summary>
    /// <typeparam name="TClass"></typeparam>
    /// <typeparam name="TProperty"></typeparam>
    /// <param name="binding"></param>
    /// <param name="value">the value to compare against, which the caller supplied</param>
    /// <param name="comparison">which way round to compare them</param>
    /// <returns></returns>
    private static Expression Ordered<TClass, TProperty>(Binding<TClass> binding, TProperty value, Func<Expression, Expression, Expression> comparison)
    {
        // The value, with any Nullable<> on the property itself already stepped through
        Expression property = binding.UnwrappedAccessor;
        Expression given = QueryValue.Of(value);

        if (binding.UnwrappedPropertyTypeIsEnum)
        {
            var underlying = Enum.GetUnderlyingType(binding.UnwrappedPropertyType);

            property = Expression.Convert(property, underlying);
            given = Expression.Convert(given, underlying);
        }

        return comparison(property, given);
    }

    /// <summary>
    /// Put the null guard in front of the test, so the test is only reached for a row that has a value. The
    /// short circuit is what makes an unwrap safe in memory, and it is what makes the result match SQL.
    /// </summary>
    private static Expression<Func<TClass, bool>> Guarded<TClass>(Binding<TClass> binding, Expression test)
    {
        return Lambda<TClass>(binding.RequiresNullCheck ? Expression.AndAlso(binding.NotNullCheck, test) : test, binding);
    }

    private static Expression<Func<TClass, bool>> Lambda<TClass>(Expression body, Binding<TClass> binding)
    {
        return Expression.Lambda<Func<TClass, bool>>(body, binding.Parameter);
    }
}
