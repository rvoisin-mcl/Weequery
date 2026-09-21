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
        // writes as words in upper case. The deprecated styles are still written: that is what deprecated means.
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

            // One spelling each, in every style. The quantifiers arrived with Native and were never given a
            // symbolic or an SQL form to be deprecated out of.
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
                throw new WeequeryException($"Operator {op} is invalid");
        }
    }

    /// <summary>
    /// Write a condition out as a query string, such that <see cref="ParseQuery"/> reads it back as an equivalent
    /// condition. The inverse of <see cref="ParseQuery"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The round trip preserves meaning, not types: values are written as text and come back as string valued
    /// conditions, exactly as <see cref="PackedCondition.Unpack()"/> produces them, and the expression builder
    /// parses them against the bound property's type when the query is built. So
    /// <c>ParseQuery(condition.ToQuery())</c> filters the same rows as <c>condition</c>, but is not necessarily
    /// the same object graph.
    /// </para>
    /// <para>
    /// One consequence worth knowing if you compare the text: a typed condition writes its values unquoted, since
    /// a number or a date reads better that way, while a condition that came from the parser holds strings and
    /// writes them quoted. So <c>ParseQuery(x.ToQuery()).ToQuery()</c> can differ from <c>x.ToQuery()</c> by the
    /// quoting, and is stable from there on. Compare parsed conditions, or the rows they select, rather than the
    /// original string.
    /// </para>
    /// </remarks>
    /// <param name="condition"></param>
    /// <param name="style">
    /// which spelling to use for the operators that have more than one. Defaults to
    /// <see cref="QueryStyle.Native"/>, which writes AND/OR/NOT/=/&lt;&gt; and one word per named operator. Every
    /// style reads back, see <see cref="QueryStyle"/>
    /// </param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">
    /// the condition cannot be expressed in the query language, which can happen for a conjunction with no operands,
    /// since the language has no way to say "match everything"
    /// </exception>
    public static string ToQuery(this ICondition condition, QueryStyle style = QueryStyle.Native)
    {
        return QueryWriter.Write(condition, style);
    }

    /// <summary>
    /// Parse a query string into a condition tree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reading is permissive by default, and has to stay that way: every spelling the language has ever accepted
    /// is sitting in somebody's saved filter, and a deprecated style is one that still works. So with no style
    /// named, <c>&amp;&amp;</c> and <c>AND</c> are both read, and so are <c>IS NULL</c>, <c>IN</c> and
    /// <c>BETWEEN</c>.
    /// </para>
    /// <para>
    /// Passing <see cref="QueryStyle.Native"/> asks for the strict grammar instead, where each operator has one
    /// spelling and every alternate is refused by name. Worth doing where the queries are yours, or where you
    /// would rather a caller heard about <c>&amp;&amp;</c> now than have it stop working later.
    /// </para>
    /// </remarks>
    /// <param name="query">eg. "(Age &gt; 20) AND NOT (Name StartsWith 'Bob')"</param>
    /// <param name="style">
    /// <see cref="QueryStyle.Native"/> to accept only the one spelling of each operator. Null, or either
    /// deprecated style, accepts every spelling, which is what reading has always done
    /// </param>
    /// <returns>null if the query is empty or whitespace</returns>
    /// <exception cref="WeequeryException">
    /// the query is malformed, or spells an operator a way the requested style does not accept
    /// </exception>
    public static ICondition? ParseQuery(string query, QueryStyle? style = null)
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
    public record NumberOfValuesRequired(int Minimum, int Maximum);

    /// <summary>
    /// Check a value count against what the operator takes.
    /// <para>
    /// Called when a condition is built and again when it is turned into an expression. The second time is not
    /// redundant: a condition holds its values in a <see cref="List{T}"/> that a caller can still add to, so what
    /// it holds when the query is built is what actually becomes parameters, and that is the count the provider
    /// will be handed.
    /// </para>
    /// </summary>
    /// <param name="op"></param>
    /// <param name="field">named in the error, since a caller building several conditions needs to know which</param>
    /// <param name="count"></param>
    /// <exception cref="WeequeryException">too few values for the operator, or too many</exception>
    internal static void ValidateValueCount(Operator op, string field, int count)
    {
        var required = GetNumberOfValuesRequiredForOperation(op);

        if (count < required.Minimum)
        {
            throw new WeequeryException($"Not enough values provided for Operator '{op}' on field '{field}', it needs at least {required.Minimum} but got {count}");
        }

        // Naming the limit matters for the IsIn family, where the maximum is a cap rather than a shape,
        // see MaxValuesInList
        if (count > required.Maximum)
        {
            throw new WeequeryException($"Extra values provided for Operator '{op}' on field '{field}', it accepts at most {required.Maximum} but got {count}");
        }
    }

    /// <summary>
    /// The values an operator takes, as a range: none for the null tests, one for a comparison, two for a range,
    /// and up to <see cref="MaxValuesInList"/> for the IsIn family. Checked wherever a condition is built, so a
    /// query string, a packed condition and a hand built one are all held to the same counts.
    /// </summary>
    /// <param name="op"></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException"></exception>
    public static NumberOfValuesRequired GetNumberOfValuesRequiredForOperation(Operator op)
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
                throw new WeequeryException($"Operator {op} is invalid");
        }
    }

    /// <summary>
    /// Which of the four comparison shapes an operator belongs to, which is the type that represents it: see
    /// <see cref="ConditionShape"/>. The counterpart of
    /// <see cref="GetNumberOfValuesRequiredForOperation"/>, and the single place that decides, so the parser,
    /// the unpacker and the condition types themselves all agree on which operator goes where.
    /// </summary>
    /// <param name="op"></param>
    /// <returns>
    /// null for <see cref="Operator.And"/>, <see cref="Operator.Or"/> and <see cref="Operator.Not"/>, which
    /// combine conditions rather than testing a bound property and so have no shape
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
            // A quantifier holds a condition rather than operands, so it has no comparison shape either
            case Operator.Any:
            case Operator.All:
            case Operator.None:
                return null;

            default:
                throw new WeequeryException($"Operator {op} is invalid");
        }
    }

    /// <summary>
    /// Build the comparison an operator's shape calls for, over operands that are already text. The general way
    /// to build one when the operator is not known until run time, and what the parser and
    /// <see cref="PackedCondition.Unpack()"/> both use, since both arrive with an operator and a list and have to
    /// land on the type that holds that many.
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

            _ => throw new WeequeryException($"Cannot determine an appropriate shape for Operator '{op}' on field '{field}'"),
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
    /// <param name="source">whether the value is something to compare against or the key of another bound property, see <see cref="ValueSource"/>. A key is a name, so it is passed as text whatever the property holds</param>
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
    /// <param name="source">whether the value is something to compare against or the key of another bound property, see <see cref="ValueSource"/>. A key is a name, so it is passed as text whatever the property holds</param>
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
    /// <param name="source">whether the value is something to compare against or the key of another bound property, see <see cref="ValueSource"/>. A key is a name, so it is passed as text whatever the property holds</param>
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
    /// <param name="source">whether the value is something to compare against or the key of another bound property, see <see cref="ValueSource"/>. A key is a name, so it is passed as text whatever the property holds</param>
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
    /// <param name="source">whether the value is something to compare against or the key of another bound property, see <see cref="ValueSource"/>. A key is a name, so it is passed as text whatever the property holds</param>
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
    /// <param name="source">whether the value is something to compare against or the key of another bound property, see <see cref="ValueSource"/>. A key is a name, so it is passed as text whatever the property holds</param>
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
    /// <param name="source1">whether value1 is something to compare against or the key of another bound property</param>
    /// <param name="value2">the high end, inclusive</param>
    /// <param name="source2">whether value2 is something to compare against or the key of another bound property</param>
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
    /// <param name="source1">whether value1 is something to compare against or the key of another bound property</param>
    /// <param name="value2">the high end, inclusive</param>
    /// <param name="source2">whether value2 is something to compare against or the key of another bound property</param>
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
    /// <param name="values">the operands to test against, each carrying whether it is a value or the key of another bound property, see <see cref="ConditionValue{T}"/></param>
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
    /// <param name="values">the operands to test against, each carrying whether it is a value or the key of another bound property, see <see cref="ConditionValue{T}"/></param>
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
    /// <param name="source">whether the value is something to compare against or the key of another bound property, see <see cref="ValueSource"/>. A key is a name, so it is passed as text whatever the property holds</param>
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
    /// <param name="source">whether the value is something to compare against or the key of another bound property, see <see cref="ValueSource"/>. A key is a name, so it is passed as text whatever the property holds</param>
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
    /// <param name="source">whether the value is something to compare against or the key of another bound property, see <see cref="ValueSource"/>. A key is a name, so it is passed as text whatever the property holds</param>
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
    /// <param name="source">whether the value is something to compare against or the key of another bound property, see <see cref="ValueSource"/>. A key is a name, so it is passed as text whatever the property holds</param>
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
    /// <param name="source">whether the value is something to compare against or the key of another bound property, see <see cref="ValueSource"/>. A key is a name, so it is passed as text whatever the property holds</param>
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
    /// <param name="source">whether the value is something to compare against or the key of another bound property, see <see cref="ValueSource"/>. A key is a name, so it is passed as text whatever the property holds</param>
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
    /// <param name="source">whether the value is something to compare against or the key of another bound property, see <see cref="ValueSource"/>. A key is a name, so it is passed as text whatever the property holds</param>
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
    /// <param name="source">whether the value is something to compare against or the key of another bound property, see <see cref="ValueSource"/>. A key is a name, so it is passed as text whatever the property holds</param>
    /// <returns></returns>
    public static IConjunctionCondition AddDoesNotMatchTest(this IConjunctionCondition conjunction, string field, string value, ValueSource source = ValueSource.Raw)
    {
        conjunction.Conditions.Add(new OneValueCondition<string>(Operator.DoesNotMatch, field, new ConditionValue<string>(source, value)));

        return conjunction;
    }

    /// <summary>
    /// Every field a condition names, including the ones its operands name, without duplicates.
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
    /// Names are compared without regard to case, as they are matched everywhere else, and the first spelling
    /// seen is the one kept.
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
    /// One level of <see cref="FieldsUsed"/>.
    /// </summary>
    /// <remarks>
    /// Stops at <see cref="ConditionNesting.MaxDepth"/> rather than throwing there. This reports what a condition
    /// names, and a tree too deep to be built is still a tree someone may want to look at; the refusal belongs to
    /// whatever tries to use it.
    /// </remarks>
    private static void Collect(ICondition? condition, Dictionary<string, string> seen, int depth)
    {
        if ((condition is null) || ConditionNesting.IsTooDeep(depth)) { return; }

        // Read where it lies rather than unpacked: the same members are already on it, and unpacking a tree only
        // to read the names off it would refuse a deep one where this can simply stop
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
        // the entity; what is inside it is not, so the walk stops here.
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
                // An operand may name another bound property rather than carrying a value, and that is a field
                // this query uses just as much as the one on the left of the operator
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