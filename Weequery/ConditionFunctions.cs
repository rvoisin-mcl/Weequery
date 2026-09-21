using Weequery.Interfaces;

namespace Weequery;

/// <summary>
/// Various helper functions and extensions for Condition classes
/// </summary>
public static class ConditionFunctions
{
    /// <summary>
    /// The friendly string for an operator in the requested style. Only the operators with more than one spelling
    /// differ, see <see cref="QueryStyle"/>.
    /// </summary>
    /// <param name="op"></param>
    /// <param name="style">defaults to <see cref="QueryStyle.Native"/>, as everything that writes does</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException"></exception>
    public static string GetOperationString(Operator op, QueryStyle style = QueryStyle.Native)
    {
        // Native and SQL agree on the comparison symbols and part company on the conjunctions, which Native
        // writes as words in upper case.
#pragma warning disable CS0618
        var symbolic = (style == QueryStyle.CSharp);
        var native = (style == QueryStyle.Native);
#pragma warning restore CS0618

        switch (op)
        {
            case Operator.IsNull:
                return "IsNull";

            case Operator.IsNotNull:
                return "IsNotNull";

            case Operator.Equals:
                return symbolic ? "==" : "=";

            case Operator.NotEqual:
                return symbolic ? "!=" : "<>";

            case Operator.LessThan:
                return "<";

            case Operator.LessThanOrEqual:
                return "<=";

            case Operator.GreaterThan:
                return ">";

            case Operator.GreaterThanOrEqual:
                return ">=";

            case Operator.IsBetween:
                return "IsBetween";

            case Operator.IsNotBetween:
                return "IsNotBetween";

            case Operator.IsIn:
                return "IsIn";

            case Operator.IsNotIn:
                return "IsNotIn";

            case Operator.StartsWith:
                return "StartsWith";

            case Operator.DoesNotStartWith:
                return "DoesNotStartWith";

            case Operator.EndsWith:
                return "EndsWith";

            case Operator.DoesNotEndWith:
                return "DoesNotEndWith";

            case Operator.Contains:
                return "Contains";

            case Operator.DoesNotContain:
                return "DoesNotContain";

            case Operator.IsMatch:
                return "IsMatch";

            case Operator.DoesNotMatch:
                return "DoesNotMatch";

            case Operator.Any:
                return "Any";

            case Operator.All:
                return "All";

            case Operator.None:
                return "None";

            case Operator.And:
                return symbolic ? "&&" : native ? "AND" : "And";

            case Operator.Or:
                return symbolic ? "||" : native ? "OR" : "Or";

            case Operator.Not:
                return symbolic ? "!" : native ? "NOT" : "Not";

            default:
                throw new WeequeryException(WeequeryError.OperatorInvalid, $"Operator {op} is invalid");
        }
    }

    /// <summary>
    /// Write a condition out as a query string, such that <see cref="ParseQuery"/> reads it back as an equivalent
    /// condition. The inverse of <see cref="ParseQuery"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The round trip preserves meaning, not types: values are written as text and come back as string valued
    /// conditions.
    /// </para>
    /// </remarks>
    /// <param name="condition"></param>
    /// <param name="style">
    /// which spelling to use for the operators that have more than one. Defaults to <see cref="QueryStyle.Native"/>
    /// </param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">
    /// the condition cannot be expressed in the query language
    /// </exception>
    public static string ToQuery(this ICondition condition, QueryStyle style = QueryStyle.Native)
    {
        return QueryWriter.Write(condition, style);
    }

    /// <summary>
    /// Parse a query string into a condition tree.
    /// </summary>
    /// <param name="query">eg. "(Age &gt; 20) AND NOT (Name StartsWith 'Bob')"</param>
    /// <param name="style">
    /// <see cref="QueryStyle.Native"/> to accept only the canon spelling of each operator. Either
    /// deprecated style accepts every spelling.
    /// </param>
    /// <returns>null if the query is empty or whitespace</returns>
    /// <exception cref="WeequeryException">
    /// the query is malformed, or spells an operator a way the requested style does not accept
    /// </exception>
    public static ICondition? ParseQuery(string query, QueryStyle style = QueryStyle.Native)
    {
        return QueryParser.Parse(query, style);
    }

    /// <summary>
    /// How many values the IsIn family will take.
    /// <para>
    /// The list becomes parameters, and a provider will only take so many: SQL Server allows about 2,100 in one
    /// statement, this cap is arbititrarily under that.
    /// </para>
    /// </summary>
    internal const int MaxValuesInList = 1000;

    /// <summary>
    /// How many values an operator takes, as an inclusive range
    /// </summary>
    /// <param name="Minimum">fewer than this is refused</param>
    /// <param name="Maximum">more than this is refused</param>
    internal record NumberOfValuesRequired(int Minimum, int Maximum);

    /// <summary>
    /// Check a value count against what the operator can use.
    /// <para>
    /// Called when a condition is built and again when it is turned into an expression. The second time is not
    /// redundant: a condition holds its values in a <see cref="List{T}"/> that a caller can still add to, so what
    /// it holds when the query is built is what actually becomes parameters
    /// </para>
    /// </summary>
    /// <param name="op"></param>
    /// <param name="field">named in the error</param>
    /// <param name="count"></param>
    /// <exception cref="WeequeryException">too few values for the operator, or too many</exception>
    internal static void ValidateValueCount(Operator op, string field, int count)
    {
        var required = GetNumberOfValuesRequiredForOperation(op);

        if (count < required.Minimum)
        {
            throw new WeequeryException(WeequeryError.OperandCount, $"Not enough values provided for Operator '{op}' on field '{field}', it needs at least {required.Minimum} but got {count}");
        }

        // IsIn has a cap, see MaxValuesInList
        if (count > required.Maximum)
        {
            throw new WeequeryException(WeequeryError.OperandCount, $"Extra values provided for Operator '{op}' on field '{field}', it accepts at most {required.Maximum} but got {count}");
        }
    }

    /// <summary>
    /// The values an operator takes, as a range. Checked wherever a condition is built.
    /// </summary>
    /// <param name="op"></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException"></exception>
    internal static NumberOfValuesRequired GetNumberOfValuesRequiredForOperation(Operator op)
    {
        switch (op)
        {
            case Operator.IsNull:
            case Operator.IsNotNull:
                return new(0, 0);

            case Operator.Equals:
            case Operator.NotEqual:
            case Operator.LessThan:
            case Operator.LessThanOrEqual:
            case Operator.GreaterThan:
            case Operator.GreaterThanOrEqual:
            case Operator.StartsWith:
            case Operator.DoesNotStartWith:
            case Operator.EndsWith:
            case Operator.DoesNotEndWith:
            case Operator.Contains:
            case Operator.DoesNotContain:
            case Operator.IsMatch:
            case Operator.DoesNotMatch:
                return new(1, 1);

            case Operator.IsBetween:
            case Operator.IsNotBetween:
                return new(2, 2);

            case Operator.IsIn:
            case Operator.IsNotIn:
                return new(0, MaxValuesInList);

            case Operator.Or:
            case Operator.And:
            case Operator.Not:
            case Operator.Any:
            case Operator.All:
            case Operator.None:
                return new(0, 0);

            default:
                throw new WeequeryException(WeequeryError.OperatorInvalid, $"Operator {op} is invalid");
        }
    }

    /// <summary>
    /// Which of the four comparison shapes an operator belongs to
    /// </summary>
    /// <param name="op"></param>
    /// <returns>
    /// null Operators which do not test conditions directly
    /// </returns>
    /// <exception cref="WeequeryException">the operator is not one of the known ones</exception>
    internal static ConditionShape? GetShapeForOperation(Operator op)
    {
        switch (op)
        {
            case Operator.IsNull:
            case Operator.IsNotNull:
                return ConditionShape.NoValue;

            case Operator.Equals:
            case Operator.NotEqual:
            case Operator.LessThan:
            case Operator.LessThanOrEqual:
            case Operator.GreaterThan:
            case Operator.GreaterThanOrEqual:
            case Operator.StartsWith:
            case Operator.DoesNotStartWith:
            case Operator.EndsWith:
            case Operator.DoesNotEndWith:
            case Operator.Contains:
            case Operator.DoesNotContain:
            case Operator.IsMatch:
            case Operator.DoesNotMatch:
                return ConditionShape.OneValue;

            case Operator.IsBetween:
            case Operator.IsNotBetween:
                return ConditionShape.TwoValue;

            case Operator.IsIn:
            case Operator.IsNotIn:
                return ConditionShape.MultipleValue;

            case Operator.Or:
            case Operator.And:
            case Operator.Not:
            case Operator.Any:
            case Operator.All:
            case Operator.None:
                return null;

            default:
                throw new WeequeryException(WeequeryError.OperatorInvalid, $"Operator {op} is invalid");
        }
    }

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

    /// <summary>
    /// Split a field into the key it names and the index it is taken at, if one is present.
    /// </summary>
    /// <param name="field">a field name, which may carry an index</param>
    /// <returns>the key, and the index or null where there is none</returns>
    /// <exception cref="WeequeryException">the field is null or empty</exception>
    public static IndexedField SplitIndex(string field)
    {
        WeequeryException.ThrowIfNullOrEmpty(field);

        return BindingLookup.SplitIndex(field);
    }

    /// <summary>
    /// Every unique field a condition names, including the ones its operands name
    /// </summary>
    /// <remarks>
    /// <para>
    /// What a caller needs to answer "which bindings does this query actually use",, and for anyone auditing which
    /// of them a given query is allowed to touch, see <see cref="BindingUse"/>.
    /// </para>
    /// <para>
    /// <b>Stops at a quantifier</b>, taking the collection's key and not the fields inside it. Those resolve
    /// against the collection's own allow-list rather than the entity's, see
    /// <see cref="Inquiry{T}.BindCollection"/>, so putting them in one flat list would say a field is bound on the
    /// entity when it is not. Walk into <see cref="QuantifiedCondition.Condition"/> and call this again to get
    /// them.
    /// </para>
    /// <para>
    /// Keys carry their index where a condition tested one, so "Tallies[apples]" comes back as it was written.
    /// Names are compared without case-insensitivily, the first variaion seen is the one used
    /// </para>
    /// </remarks>
    /// <param name="condition">null gives an empty list</param>
    /// <returns>never null, in the order the fields were first met</returns>
    public static List<string> FieldsUsed(this ICondition? condition)
    {
        Dictionary<string, string> seen = new(BindingLookup.KeyComparer);

        Collect(condition, seen, 0);

        return [.. seen.Values];
    }

    /// <summary>
    /// Gather one level of <see cref="FieldsUsed"/>.
    /// </summary>
    /// <remarks>
    /// Stops at <see cref="ConditionNesting.MaxDepth"/>
    /// </remarks>
    private static void Collect(ICondition? condition, Dictionary<string, string> seen, int depth)
    {
        if ((condition is null) || ConditionNesting.IsTooDeep(depth)) { return; }

        if (condition is PackedCondition packed)
        {
            Keep(seen, Indexed(packed.Field, packed.Index));

            // A quantifier's children are scoped to the collection's own allow-list, not the entity's
            if (QuantifiedCondition.IsQuantifier(packed.Operator)) { return; }

            foreach (var operand in packed.Values.Where(operand => operand.NamesProperty))
            {
                Keep(seen, operand.Value);
            }

            foreach (var child in packed.Conditions)
            {
                Collect(child, seen, depth + 1);
            }

            return;
        }

        // Names a collection and holds a condition scoped to one of its elements. The collection is a field of
        // the entity; what is inside it is not, stop here
        if (condition is QuantifiedCondition quantified)
        {
            Keep(seen, quantified.Field);
            return;
        }

        if (condition is IBound bound)
        {
            Keep(seen, Indexed(bound.Field, bound.Index));

            if (condition is IBoundCondition valued)
            {
                // An operand can use bindings on both sides of the operator
                foreach (var operand in valued.StringifyOperands().Where(operand => operand.NamesProperty))
                {
                    Keep(seen, operand.Value);
                }
            }

            return;
        }

        if (condition is IConditionContainer<ICondition> container)
        {
            foreach (var child in container.Conditions)
            {
                Collect(child, seen, depth + 1);
            }
        }
    }

    /// <summary>
    /// Every operator a condition uses, including the ones inside a quantifier.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The counterpart of <see cref="FieldsUsed"/>
    /// </para>
    /// <para>
    /// Structural operators count too, <see cref="Operator.And"/>, <see cref="Operator.Or"/> and
    /// <see cref="Operator.Not"/>, since something has to evaluate those as well. See
    /// <see cref="OperatorSupport"/>, which is what this exists for.
    /// </para>
    /// </remarks>
    /// <param name="condition">null gives an empty list</param>
    /// <returns>each operator once, in the order it was first met; never null</returns>
    public static List<Operator> OperatorsUsed(this ICondition? condition)
    {
        List<Operator> seen = []; // return in order discovered

        foreach (var used in Used(condition, 0))
        {
            if (!seen.Contains(used.Operator)) { seen.Add(used.Operator); }
        }

        return seen;
    }

    /// <summary>
    /// The first operator in a condition that <paramref name="support"/> does not allow, and the field it was
    /// used on where it named one.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="OperatorsUsed"/> so the refusal can name where it happened, which a list of
    /// operators cannot. Stops at the first, for the reason every other validation stops at the first: see the
    /// remarks on <see cref="ValidationResult"/>.
    /// </remarks>
    /// <param name="condition"></param>
    /// <param name="support"></param>
    /// <returns>null where every operator used is allowed, which includes the condition that is null</returns>
    internal static UsedOperator? FirstUnsupported(ICondition? condition, OperatorSupport support)
    {
        foreach (var used in Used(condition, 0))
        {
            if (!support.Allows(used.Operator)) { return used; }
        }

        return null;
    }

    /// <summary>
    /// Walk a condition, yielding each operator as it is met with the field it was used on.
    /// </summary>
    /// <remarks>
    /// Stops at <see cref="ConditionNesting.MaxDepth"/>, as the field walk does, so a tree deep enough to
    /// overflow the stack is refused for its depth rather than by falling over here.
    /// </remarks>
    /// <param name="condition"></param>
    /// <param name="depth">levels entered to get to this condition</param>
    /// <returns>one entry per condition met, in the order met, with duplicates left in</returns>
    private static IEnumerable<UsedOperator> Used(ICondition? condition, int depth)
    {
        if ((condition is null) || ConditionNesting.IsTooDeep(depth)) { yield break; }

        yield return new UsedOperator(condition.Operator, (condition as IBound)?.Field);

        // Two shapes of container, since a packed tree holds packed children, see IConditionContainer
        IEnumerable<ICondition> children = condition switch
        {
            IConditionContainer<ICondition> container => container.Conditions,
            IConditionContainer<PackedCondition> packed => packed.Conditions,
            _ => [],
        };

        foreach (var child in children)
        {
            foreach (var used in Used(child, depth + 1)) { yield return used; }
        }
    }

    /// <summary>
    /// A field with its index put back on, which is how a key carrying one is written everywhere else
    /// </summary>
    private static string Indexed(string field, string? index)
    {
        return (index is null) ? field : $"{field}[{index}]";
    }

    /// <summary>
    /// Keep the first spelling of a name, since two that differ only in case are one field
    /// </summary>
    private static void Keep(Dictionary<string, string> seen, string field)
    {
        if (!string.IsNullOrEmpty(field)) { seen.TryAdd(field, field); }
    }
}
