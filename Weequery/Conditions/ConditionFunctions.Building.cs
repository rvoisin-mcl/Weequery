using Weequery.Interfaces;

namespace Weequery;

// Building a condition by hand rather than reading one. Each of these adds a single test to a conjunction and
// returns it, so a filter assembled in code reads as one sentence; BuildComparison is what they share.
public static partial class ConditionFunctions
{
    /// <summary>
    /// Build the comparison an operator's shape calls for, over operands that are already text. The general way
    /// to build one when the operator is not known until runtime.
    /// </summary>
    /// <param name="op"></param>
    /// <param name="field"></param>
    /// <param name="operands">checked against the operator before any of them is read</param>
    /// <param name="index">[OPT] which element of the collection to test, see <see cref="IBound.Index"/></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">the operator has no shape, or the count does not suit it</exception>
    public static IBoundCondition BuildComparison(Operator op, string field, List<ConditionValue<string>> operands, string? index = null)
    {
        WeequeryException.ThrowIfNull(operands);

        ValidateValueCount(op, field, operands.Count);

        return GetShapeForOperation(op) switch
        {
            ConditionShape.NoValue => new NoValueCondition(op, field, index),
            ConditionShape.OneValue => new OneValueCondition<string>(op, field, operands[0], index),
            ConditionShape.TwoValue => new TwoValueCondition<string>(op, field, operands[0], operands[1], index),
            ConditionShape.MultipleValue => new MultipleValueCondition<string>(op, field, operands, index),

            _ => throw new WeequeryException(WeequeryError.OperatorInvalid, $"Cannot determine an appropriate shape for Operator '{op}' on field '{field}'"),
        };
    }

    /// <summary>
    /// Add a NoValueCondition to test a field for is null to the conjunction
    /// </summary>
    /// <param name="conjunction"></param>
    /// <param name="field"></param>
    /// <returns></returns>
    public static IConjunctionCondition AddIsNullTest(this IConjunctionCondition conjunction, string field)
    {
        conjunction.Conditions.Add(new NoValueCondition(Operator.IsNull, field));

        return conjunction;
    }

    /// <summary>
    /// Add a comparison to test a field for is NOT null to the conjunction
    /// </summary>
    /// <param name="conjunction"></param>
    /// <param name="field"></param>
    /// <returns></returns>
    public static IConjunctionCondition AddIsNotNullTest(this IConjunctionCondition conjunction, string field)
    {
        conjunction.Conditions.Add(new NoValueCondition(Operator.IsNotNull, field));

        return conjunction;
    }

    /// <summary>
    /// Add a comparison to test a field is equal to a value to the conjunction
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="conjunction"></param>
    /// <param name="field"></param>
    /// <param name="value"></param>
    /// <param name="source">if the value is something to compare against or the key of another bound property, see <see cref="ValueSource"/></param>
    /// <returns></returns>
    public static IConjunctionCondition AddIsEqualTest<T>(this IConjunctionCondition conjunction, string field, T value, ValueSource source = ValueSource.Raw)
    {
        conjunction.Conditions.Add(new OneValueCondition<T>(Operator.Equals, field, new ConditionValue<T>(source, value)));

        return conjunction;
    }

    /// <summary>
    /// Add a comparison to test a field is NOT equal to a value to the conjunction
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="conjunction"></param>
    /// <param name="field"></param>
    /// <param name="value"></param>
    /// <param name="source">if the value is something to compare against or the key of another bound property, see <see cref="ValueSource"/></param>
    /// <returns></returns>
    public static IConjunctionCondition AddIsNotEqualTest<T>(this IConjunctionCondition conjunction, string field, T value, ValueSource source = ValueSource.Raw)
    {
        conjunction.Conditions.Add(new OneValueCondition<T>(Operator.NotEqual, field, new ConditionValue<T>(source, value)));

        return conjunction;
    }

    /// <summary>
    /// Add a comparison to test a field is less than a value to the conjunction
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="conjunction"></param>
    /// <param name="field"></param>
    /// <param name="value"></param>
    /// <param name="source">if the value is something to compare against or the key of another bound property, see <see cref="ValueSource"/></param>
    /// <returns></returns>
    public static IConjunctionCondition AddIsLessThanTest<T>(this IConjunctionCondition conjunction, string field, T value, ValueSource source = ValueSource.Raw)
    {
        conjunction.Conditions.Add(new OneValueCondition<T>(Operator.LessThan, field, new ConditionValue<T>(source, value)));

        return conjunction;
    }

    /// <summary>
    /// Add a comparison to test a field is less than or equal to a value to the conjunction
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="conjunction"></param>
    /// <param name="field"></param>
    /// <param name="value"></param>
    /// <param name="source">if the value is something to compare against or the key of another bound property, see <see cref="ValueSource"/></param>
    /// <returns></returns>
    public static IConjunctionCondition AddIsLessThanOrEqualToTest<T>(this IConjunctionCondition conjunction, string field, T value, ValueSource source = ValueSource.Raw)
    {
        conjunction.Conditions.Add(new OneValueCondition<T>(Operator.LessThanOrEqual, field, new ConditionValue<T>(source, value)));

        return conjunction;
    }

    /// <summary>
    /// Add a comparison to test a field is greater than a value to the conjunction
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="conjunction"></param>
    /// <param name="field"></param>
    /// <param name="value"></param>
    /// <param name="source">if the value is something to compare against or the key of another bound property, see <see cref="ValueSource"/></param>
    /// <returns></returns>
    public static IConjunctionCondition AddIsGreaterThanTest<T>(this IConjunctionCondition conjunction, string field, T value, ValueSource source = ValueSource.Raw)
    {
        conjunction.Conditions.Add(new OneValueCondition<T>(Operator.GreaterThan, field, new ConditionValue<T>(source, value)));

        return conjunction;
    }

    /// <summary>
    /// Add a comparison to test a field is greater than or equal to a value to the conjunction
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="conjunction"></param>
    /// <param name="field"></param>
    /// <param name="value"></param>
    /// <param name="source">if the value is something to compare against or the key of another bound property, see <see cref="ValueSource"/></param>
    /// <returns></returns>
    public static IConjunctionCondition AddIsGreaterThanOrEqualToTest<T>(this IConjunctionCondition conjunction, string field, T value, ValueSource source = ValueSource.Raw)
    {
        conjunction.Conditions.Add(new OneValueCondition<T>(Operator.GreaterThanOrEqual, field, new ConditionValue<T>(source, value)));

        return conjunction;
    }

    /// <summary>
    /// Add a comparison to test a field is between the specified values to the conjunction
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="conjunction"></param>
    /// <param name="field"></param>
    /// <param name="value1"></param>
    /// <param name="value2"></param>
    /// <returns></returns>
    public static IConjunctionCondition AddIsBetweenTest<T>(this IConjunctionCondition conjunction, string field, T value1, T value2)
    {
        conjunction.Conditions.Add(new TwoValueCondition<T>(Operator.IsBetween, field, value1, value2));

        return conjunction;
    }

    /// <summary>
    /// Add a comparison to test a field is between the specified operands to the conjunction, where either end may
    /// be another bound property
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="conjunction"></param>
    /// <param name="field"></param>
    /// <param name="value1">the low end, inclusive</param>
    /// <param name="source1">if value1 is something to compare against or the key of another bound property</param>
    /// <param name="value2">the high end, inclusive</param>
    /// <param name="source2">if value2 is something to compare against or the key of another bound property</param>
    /// <returns></returns>
    public static IConjunctionCondition AddIsBetweenTest<T>(this IConjunctionCondition conjunction, string field, T value1, ValueSource source1, T value2, ValueSource source2)
    {
        conjunction.Conditions.Add(new TwoValueCondition<T>(Operator.IsBetween, field, new ConditionValue<T>(source1, value1), new ConditionValue<T>(source2, value2)));

        return conjunction;
    }

    /// <summary>
    /// Add a comparison to test a field is NOT between the specified values to the conjunction
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="conjunction"></param>
    /// <param name="field"></param>
    /// <param name="value1"></param>
    /// <param name="value2"></param>
    /// <returns></returns>
    public static IConjunctionCondition AddIsNotBetweenTest<T>(this IConjunctionCondition conjunction, string field, T value1, T value2)
    {
        conjunction.Conditions.Add(new TwoValueCondition<T>(Operator.IsNotBetween, field, value1, value2));

        return conjunction;
    }

    /// <summary>
    /// Add a comparison to test a field is NOT between the specified operands to the conjunction, where either end
    /// may be another bound property
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="conjunction"></param>
    /// <param name="field"></param>
    /// <param name="value1">the low end, inclusive</param>
    /// <param name="source1">if value1 is something to compare against or the key of another bound property</param>
    /// <param name="value2">the high end, inclusive</param>
    /// <param name="source2">if value2 is something to compare against or the key of another bound property</param>
    /// <returns></returns>
    public static IConjunctionCondition AddIsNotBetweenTest<T>(this IConjunctionCondition conjunction, string field, T value1, ValueSource source1, T value2, ValueSource source2)
    {
        conjunction.Conditions.Add(new TwoValueCondition<T>(Operator.IsNotBetween, field, new ConditionValue<T>(source1, value1), new ConditionValue<T>(source2, value2)));

        return conjunction;
    }

    /// <summary>
    /// Add a comparison to test a field is one of the specified values to the conjunction, each of them a value
    /// rather than the key of a property
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="conjunction"></param>
    /// <param name="field"></param>
    /// <param name="values"></param>
    /// <returns></returns>
    public static IConjunctionCondition AddIsInTest<T>(this IConjunctionCondition conjunction, string field, IEnumerable<T> values)
    {
        conjunction.Conditions.Add(new MultipleValueCondition<T>(Operator.IsIn, field, values.ToList()));

        return conjunction;
    }

    /// <summary>
    /// Add a comparison to test a field is one of the specified operands to the conjunction, any of which may be
    /// another bound property
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="conjunction"></param>
    /// <param name="field"></param>
    /// <param name="values">the operands to test against, each carrying if it is a value or the key of another bound property, see <see cref="ConditionValue{T}"/></param>
    /// <returns></returns>
    public static IConjunctionCondition AddIsInTest<T>(this IConjunctionCondition conjunction, string field, IEnumerable<ConditionValue<T>> values)
    {
        conjunction.Conditions.Add(new MultipleValueCondition<T>(Operator.IsIn, field, values.ToList()));

        return conjunction;
    }

    /// <summary>
    /// Add a comparison to test a field is NOT one of the specified values to the conjunction, each of them a value
    /// rather than the key of a property
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="conjunction"></param>
    /// <param name="field"></param>
    /// <param name="values"></param>
    /// <returns></returns>
    public static IConjunctionCondition AddIsNotInTest<T>(this IConjunctionCondition conjunction, string field, IEnumerable<T> values)
    {
        conjunction.Conditions.Add(new MultipleValueCondition<T>(Operator.IsNotIn, field, values.ToList()));

        return conjunction;
    }

    /// <summary>
    /// Add a comparison to test a field is NOT one of the specified operands to the conjunction, any of which may
    /// be another bound property
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="conjunction"></param>
    /// <param name="field"></param>
    /// <param name="values">the operands to test against, each carrying if it is a value or the key of another bound property, see <see cref="ConditionValue{T}"/></param>
    /// <returns></returns>
    public static IConjunctionCondition AddIsNotInTest<T>(this IConjunctionCondition conjunction, string field, IEnumerable<ConditionValue<T>> values)
    {
        conjunction.Conditions.Add(new MultipleValueCondition<T>(Operator.IsNotIn, field, values.ToList()));

        return conjunction;
    }

    /// <summary>
    /// Add a comparison to test a field begins with the specified value to the conjunction
    /// </summary>
    /// <param name="conjunction"></param>
    /// <param name="field"></param>
    /// <param name="value"></param>
    /// <param name="source">if the value is something to compare against or the key of another bound property, see <see cref="ValueSource"/></param>
    /// <returns></returns>
    public static IConjunctionCondition AddStartsWithTest(this IConjunctionCondition conjunction, string field, string value, ValueSource source = ValueSource.Raw)
    {
        conjunction.Conditions.Add(new OneValueCondition<string>(Operator.StartsWith, field, new ConditionValue<string>(source, value)));

        return conjunction;
    }

    /// <summary>
    /// Add a comparison to test a field does NOT begin with the specified value to the conjunction
    /// </summary>
    /// <param name="conjunction"></param>
    /// <param name="field"></param>
    /// <param name="value"></param>
    /// <param name="source">if the value is something to compare against or the key of another bound property, see <see cref="ValueSource"/></param>
    /// <returns></returns>
    public static IConjunctionCondition AddDoesNotStartWithTest(this IConjunctionCondition conjunction, string field, string value, ValueSource source = ValueSource.Raw)
    {
        conjunction.Conditions.Add(new OneValueCondition<string>(Operator.DoesNotStartWith, field, new ConditionValue<string>(source, value)));

        return conjunction;
    }

    /// <summary>
    /// Add a comparison to test a field ends with the specified value to the conjunction
    /// </summary>
    /// <param name="conjunction"></param>
    /// <param name="field"></param>
    /// <param name="value"></param>
    /// <param name="source">if the value is something to compare against or the key of another bound property, see <see cref="ValueSource"/></param>
    /// <returns></returns>
    public static IConjunctionCondition AddEndsWithTest(this IConjunctionCondition conjunction, string field, string value, ValueSource source = ValueSource.Raw)
    {
        conjunction.Conditions.Add(new OneValueCondition<string>(Operator.EndsWith, field, new ConditionValue<string>(source, value)));

        return conjunction;
    }

    /// <summary>
    /// Add a comparison to test a field does NOT end with the specified value to the conjunction
    /// </summary>
    /// <param name="conjunction"></param>
    /// <param name="field"></param>
    /// <param name="value"></param>
    /// <param name="source">if the value is something to compare against or the key of another bound property, see <see cref="ValueSource"/></param>
    /// <returns></returns>
    public static IConjunctionCondition AddDoesNotEndWithTest(this IConjunctionCondition conjunction, string field, string value, ValueSource source = ValueSource.Raw)
    {
        conjunction.Conditions.Add(new OneValueCondition<string>(Operator.DoesNotEndWith, field, new ConditionValue<string>(source, value)));

        return conjunction;
    }

    /// <summary>
    /// Add a comparison to test a field contains the specified value to the conjunction
    /// </summary>
    /// <param name="conjunction"></param>
    /// <param name="field"></param>
    /// <param name="value"></param>
    /// <param name="source">if the value is something to compare against or the key of another bound property, see <see cref="ValueSource"/></param>
    /// <returns></returns>
    public static IConjunctionCondition AddContainsTest(this IConjunctionCondition conjunction, string field, string value, ValueSource source = ValueSource.Raw)
    {
        conjunction.Conditions.Add(new OneValueCondition<string>(Operator.Contains, field, new ConditionValue<string>(source, value)));

        return conjunction;
    }

    /// <summary>
    /// Add a comparison to test a field does NOT contain the specified value to the conjunction
    /// </summary>
    /// <param name="conjunction"></param>
    /// <param name="field"></param>
    /// <param name="value"></param>
    /// <param name="source">if the value is something to compare against or the key of another bound property, see <see cref="ValueSource"/></param>
    /// <returns></returns>
    public static IConjunctionCondition AddDoesNotContainTest(this IConjunctionCondition conjunction, string field, string value, ValueSource source = ValueSource.Raw)
    {
        conjunction.Conditions.Add(new OneValueCondition<string>(Operator.DoesNotContain, field, new ConditionValue<string>(source, value)));

        return conjunction;
    }

    /// <summary>
    /// Add an arbitrary condition to the conjunction
    /// </summary>
    /// <param name="conjunction"></param>
    /// <param name="condition"></param>
    /// <returns></returns>
    public static IConjunctionCondition AddCondition(this IConjunctionCondition conjunction, ICondition condition)
    {
        conjunction.Conditions.Add(condition);

        return conjunction;
    }

    /// <summary>
    /// Add a comparison to test a field matches (via regex) the specified value to the conjunction
    /// </summary>
    /// <param name="conjunction"></param>
    /// <param name="field"></param>
    /// <param name="value"></param>
    /// <param name="source">if the value is something to compare against or the key of another bound property, see <see cref="ValueSource"/></param>
    /// <returns></returns>
    public static IConjunctionCondition AddIsMatchTest(this IConjunctionCondition conjunction, string field, string value, ValueSource source = ValueSource.Raw)
    {
        conjunction.Conditions.Add(new OneValueCondition<string>(Operator.IsMatch, field, new ConditionValue<string>(source, value)));

        return conjunction;
    }

    /// <summary>
    /// Add a comparison to test a field does NOT match (via regex) the specified value to the conjunction
    /// </summary>
    /// <param name="conjunction"></param>
    /// <param name="field"></param>
    /// <param name="value"></param>
    /// <param name="source">if the value is something to compare against or the key of another bound property, see <see cref="ValueSource"/></param>
    /// <returns></returns>
    public static IConjunctionCondition AddDoesNotMatchTest(this IConjunctionCondition conjunction, string field, string value, ValueSource source = ValueSource.Raw)
    {
        conjunction.Conditions.Add(new OneValueCondition<string>(Operator.DoesNotMatch, field, new ConditionValue<string>(source, value)));

        return conjunction;
    }

}
