using System.Collections;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Weequery.Interfaces;

namespace Weequery.Builders;

/// <summary>
/// Builds the expression for a comparison with another bound property among the things it compares against.
/// </summary>
/// <remarks>
/// <para>
/// Kept apart from the per type builders because those are chosen by one binding's type, and this has more than
/// one. Every operand has to be that same type, so one place can decide by it: a string compares the way the
/// string builder compares, everything else the way the value builders do, with an enum stepped down to what it is
/// based on first.
/// </para>
/// <para>
/// The operands can be mixed, since only one of them has to be a property: "Pay IsBetween ([Floor], 20000)" is a
/// range with one end from the row and one from the query. A value operand is parsed against the same type and
/// parameterized exactly as an ordinary condition's value is, so it lands in the SQL as a parameter rather than a
/// literal.
/// </para>
/// <para>
/// The null rule is the one the rest of the operators follow, applied to every property involved: a row where any
/// of them has no value matches nothing, the negative operators included, which is what a database answers when
/// either side of a comparison is NULL. IsIn is the one shape where that is not what a database answers, since a
/// test that fails among others that pass changes nothing, so it guards each of its tests instead, see
/// <see cref="Membership"/>.
/// </para>
/// </remarks>
internal static class FieldComparison
{
    /// <summary>
    /// One thing the field is compared against, already resolved to what it is.
    /// </summary>
    /// <typeparam name="TClass"></typeparam>
    private sealed class Operand<TClass>
    {
        /// <summary>The property this reads, or null when the operand is a value</summary>
        public Binding<TClass>? Property { get; init; }

        /// <summary>The parsed value, or null when the operand is a property</summary>
        public object? Value { get; init; }

        /// <summary>What to compare against, either way</summary>
        public required Expression Expression { get; init; }
    }

    /// <summary>
    /// left op operands, guarded on every property involved.
    /// </summary>
    /// <typeparam name="TClass"></typeparam>
    /// <param name="left">the binding the condition names</param>
    /// <param name="condition">the comparison, at least one of whose operands names another bound property</param>
    /// <param name="values">the condition's operands, already read as text</param>
    /// <param name="bindings">to resolve the properties the condition's values name</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">
    /// a named property is unbound, an operand cannot be compared with the field, or the operator does not apply
    /// </exception>
    internal static Expression<Func<TClass, bool>> Build<TClass>(Binding<TClass> left, IBoundCondition condition, List<ConditionValue<string>> values, Dictionary<string, Binding<TClass>> bindings)
    {
        WeequeryException.ThrowIfNull(left);
        WeequeryException.ThrowIfNull(condition);

        // The list a condition of many values holds is public, so the count is checked here as it is for any
        // other condition
        ConditionFunctions.ValidateValueCount(condition.Operator, condition.Field, values.Count);

        RefuseUncomparableType(left, condition.Operator);

        var operands = (from value in values select Resolve(left, value, condition.Field, bindings)).ToList();

        var comparison = condition.Operator switch
        {
            Operator.IsBetween => Range(left, operands),
            Operator.IsNotBetween => Expression.Not(Range(left, operands)),

            Operator.IsIn => Membership(left, operands, guardEachOperand: true),
            Operator.IsNotIn => Expression.Not(Membership(left, operands, guardEachOperand: false)),

            _ => Compare(left, condition.Operator, left.UnwrappedAccessor, operands[0].Expression),
        };

        // Any property involved having no value is a comparison with nothing to say, so neither the operator nor
        // its negation holds. IsIn is the exception, and answers what SQL answers instead, see Membership.
        var guard = Guard(left, (condition.Operator == Operator.IsIn) ? [] : operands);
        var guarded = (guard is null) ? comparison : Expression.AndAlso(guard, comparison);

        return Expression.Lambda<Func<TClass, bool>>(guarded, left.Parameter);
    }

    /// <summary>
    /// Refuse a property of a type nothing can be compared with, so that naming one on either side of a comparison
    /// is refused the same way as comparing it against a value.
    /// </summary>
    /// <remarks>
    /// A type Weequery has no builder for is one it cannot compare, and a reference type it does not support
    /// arrives here as <see cref="object"/>, which supports only the null tests, see
    /// <see cref="ObjectExpressionBuilder"/>. Both would otherwise reach the expression api and be refused by it
    /// instead, which says the same thing in terms of the types the tree is made of rather than the field the
    /// caller named.
    /// </remarks>
    /// <exception cref="WeequeryException"></exception>
    private static void RefuseUncomparableType<TClass>(Binding<TClass> left, Operator op)
    {
        if (left.UnwrappedPropertyType == typeof(object))
        {
            throw new WeequeryException($"Operator {op} is unsupported for Binding '{left.PropertyPath}': a {typeof(object).Name} is not something to compare, only the null tests apply to it");
        }

        if (!ExpressionBuilder.HasBuilderForBinding(left))
        {
            throw new WeequeryException($"No expression builder available for: '{left.UnwrappedPropertyType.Name}'");
        }
    }

    /// <summary>
    /// Work out what one of the condition's values is: the property it names, or a value of the field's own type
    /// read from the text.
    /// </summary>
    /// <exception cref="WeequeryException">the property is unbound or of another type, or the text is not a value of the field's type</exception>
    private static Operand<TClass> Resolve<TClass>(Binding<TClass> left, ConditionValue<string> value, string field, Dictionary<string, Binding<TClass>> bindings)
    {
        if (!value.NamesProperty)
        {
            var parsed = Parse(left, value.Value, field);

            return new Operand<TClass> { Value = parsed, Expression = QueryValue.OfType(left.UnwrappedPropertyType, parsed) };
        }

        if (!bindings.TryGetValue(value.Value, out var property))
        {
            throw new WeequeryException($"Unbound field: '{value.Value}'");
        }

        // The expression api compares like with like, and promoting one side to the other would mean deciding
        // which widens to which for every pair of types, including the pairs C# itself refuses. Same type only.
        if (left.UnwrappedPropertyType != property.UnwrappedPropertyType)
        {
            throw new WeequeryException($"Cannot compare '{left.PropertyPath}' with '{property.PropertyPath}': one is a {left.UnwrappedPropertyType.Name} and the other a {property.UnwrappedPropertyType.Name}");
        }

        return new Operand<TClass> { Property = property, Expression = property.UnwrappedAccessor };
    }

    /// <summary>
    /// A value operand is text, the same as any condition off the wire carries, so it is read against the field's
    /// type here. Parsed once and boxed, so it reaches the provider as a parameter, see <see cref="QueryValue"/>.
    /// </summary>
    private static object Parse<TClass>(Binding<TClass> left, string text, string field)
    {
        try
        {
            return ValueFormat.Parse(left.UnwrappedPropertyType, text);
        }
        catch (WeequeryException ex)
        {
            throw new WeequeryException($"{ex.Message}, for field '{field}'", ex);
        }
        catch (Exception ex)
        {
            throw new WeequeryException($"Failed to parse a {left.UnwrappedPropertyType.Name} for field '{field}': {ex.Message}", ex);
        }
    }


    /// <summary>
    /// True when the field and every property it is compared against has a value, or null when none of them can be
    /// missing. One property named twice is guarded once, since the same binding is the same test.
    /// </summary>
    /// <param name="left"></param>
    /// <param name="operands">the properties to guard, which is none of them where the operator guards its own</param>
    private static Expression? Guard<TClass>(Binding<TClass> left, List<Operand<TClass>> operands)
    {
        var checks = new List<Expression>();

        if (left.RequiresNullCheck) { checks.Add(left.NotNullCheck); }

        foreach (var property in (from operand in operands where operand.Property is not null select operand.Property).Distinct())
        {
            if (property!.RequiresNullCheck && (property != left)) { checks.Add(property.NotNullCheck); }
        }

        return (checks.Count == 0) ? null : checks.Aggregate(Expression.AndAlso);
    }

    /// <summary>
    /// field >= low AND field &lt;= high, inclusive of both ends as the range is everywhere else
    /// </summary>
    private static Expression Range<TClass>(Binding<TClass> left, List<Operand<TClass>> operands)
    {
        return Expression.AndAlso(
            Compare(left, Operator.GreaterThanOrEqual, left.UnwrappedAccessor, operands[0].Expression),
            Compare(left, Operator.LessThanOrEqual, left.UnwrappedAccessor, operands[1].Expression));
    }

    /// <summary>
    /// Whether the field is any of the operands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The values are kept together as a list the provider takes as one parameter, for the reason
    /// <see cref="ExpressionBuilderFunctions"/> gives: one statement, and so one plan, whatever the length of the
    /// list. A property cannot go in that list, since it is a column rather than something to send, so each named
    /// property is one equality test ORed on. That does make the statement depend on how many properties were
    /// named, which the length of the value list still does not.
    /// </para>
    /// <para>
    /// Where the tests are ORed, a missing property is left to fail its own test rather than suppressing the whole
    /// row, which is what a database answers: "X IN (v) OR X = other" with a null other is TRUE for a row matching
    /// v, since TRUE OR UNKNOWN is TRUE. Guarding each test rather than the condition gives FALSE where SQL gives
    /// UNKNOWN, and at the top of an OR those are the same answer, so both evaluators agree with the database.
    /// The guard is still needed on each test: without it a missing operand would compare equal to a missing
    /// field, and unwrapping a Nullable&lt;&gt; that holds nothing throws.
    /// </para>
    /// <para>
    /// Negated, that stops being true: NOT(FALSE) is TRUE where NOT(UNKNOWN) is UNKNOWN, so IsNotIn keeps the
    /// guard in front of the whole condition, which is what makes <em>it</em> answer what SQL answers. The two
    /// operators differing here is SQL's own asymmetry, not one invented for them.
    /// </para>
    /// </remarks>
    /// <param name="left"></param>
    /// <param name="operands"></param>
    /// <param name="guardEachOperand">
    /// true where this is the whole condition, false where the caller has guarded them in front of it
    /// </param>
    private static Expression Membership<TClass>(Binding<TClass> left, List<Operand<TClass>> operands, bool guardEachOperand)
    {
        var tests = new List<Expression>();

        var values = (from operand in operands where operand.Property is null select operand.Value!).ToList();
        if (values.Count > 0) { tests.Add(Contains(left, values)); }

        foreach (var operand in from operand in operands where operand.Property is not null select operand)
        {
            var test = Compare(left, Operator.Equals, left.UnwrappedAccessor, operand.Expression);

            tests.Add((guardEachOperand && operand.Property!.RequiresNullCheck)
                ? Expression.AndAlso(operand.Property.NotNullCheck, test)
                : test);
        }

        // Never empty: a condition of this type has at least one operand, and at least one of them is a property
        return tests.Aggregate(Expression.OrElse);
    }

    /// <summary>
    /// One List&lt;T&gt; and its Contains per property type, kept for the life of the process rather than reflected
    /// over on every query that compares against a list
    /// </summary>
    private static readonly ConcurrentDictionary<Type, (Type List, MethodInfo Contains)> ListTypes = new();

    /// <summary>
    /// values.Contains(field), on a list of the field's own type
    /// </summary>
    private static Expression Contains<TClass>(Binding<TClass> left, List<object> values)
    {
        var listType = ListTypes.GetOrAdd(left.UnwrappedPropertyType, static forType =>
        {
            var type = typeof(List<>).MakeGenericType(forType);

            return (type, type.GetMethod(nameof(List<object>.Contains), [forType])
                ?? throw new WeequeryException($"(Should be impossible) No List<{forType.Name}>.Contains method"));
        });

        // Non generic, because the element type is only known here. It is still a List<T> underneath, which is
        // what the provider needs to see.
        var list = (IList?)Activator.CreateInstance(listType.List)
            ?? throw new WeequeryException($"(Should be impossible) Could not hold a list of {left.UnwrappedPropertyType.Name}");

        foreach (var value in values) { list.Add(value); }

        return Expression.Call(QueryValue.OfType(listType.List, list), listType.Contains, left.UnwrappedAccessor);
    }

    /// <summary>
    /// One comparison between two operands of the property's type, whichever of them came from the row.
    /// </summary>
    /// <exception cref="WeequeryException">the operator does not apply to that type</exception>
    private static Expression Compare<TClass>(Binding<TClass> left, Operator op, Expression one, Expression other)
    {
        var comparison = (left.UnwrappedPropertyType == typeof(string))
            ? CompareStrings(op, one, other)
            : CompareValues(left, op, one, other);

        return comparison
            ?? throw new WeequeryException($"Operator {op} cannot compare '{left.PropertyPath}' with another {left.UnwrappedPropertyType.Name}");
    }

    /// <summary>
    /// Equality and the ordering operators, on operands with any Nullable&lt;&gt; stepped through and an enum
    /// stepped down to what it is based on
    /// </summary>
    /// <returns>null if the operator does not apply to a pair of values</returns>
    private static Expression? CompareValues<TClass>(Binding<TClass> left, Operator op, Expression one, Expression other)
    {
        // A bool orders no better against another property than it does against a value
        if ((left.UnwrappedPropertyType == typeof(bool)) && (op is not (Operator.Equals or Operator.NotEqual)))
        {
            throw new WeequeryException($"Operator {op} is unsupported for the bool property '{left.PropertyPath}', only equality applies to a truth value");
        }

        if (left.UnwrappedPropertyTypeIsEnum)
        {
            var underlying = Enum.GetUnderlyingType(left.UnwrappedPropertyType);

            one = Expression.Convert(one, underlying);
            other = Expression.Convert(other, underlying);
        }

        return op switch
        {
            Operator.Equals => Expression.Equal(one, other),
            Operator.NotEqual => Expression.NotEqual(one, other),
            Operator.LessThan => Expression.LessThan(one, other),
            Operator.LessThanOrEqual => Expression.LessThanOrEqual(one, other),
            Operator.GreaterThan => Expression.GreaterThan(one, other),
            Operator.GreaterThanOrEqual => Expression.GreaterThanOrEqual(one, other),

            _ => null,
        };
    }

    /// <summary>
    /// The same operators for two strings, plus the substring family, built from the string methods rather than
    /// from operators a string does not have
    /// </summary>
    /// <returns>null if the operator does not apply to a pair of strings</returns>
    private static Expression? CompareStrings(Operator op, Expression one, Expression other)
    {
        var substring = StringMethods.Substring(op);
        if (substring is not null)
        {
            var call = Expression.Call(one, substring, other);

            return (StringMethods.IsNegated(op)) ? Expression.Not(call) : call;
        }

        // The pattern can come from the row as readily as from the query: "Name IsMatch [Pattern]". Negated inside
        // the guard the caller puts in front of this, so a missing value still matches nothing either way.
        if (op is Operator.IsMatch or Operator.DoesNotMatch)
        {
            var match = Expression.Call(StringMethods.IsMatch, one, other);

            return (op is Operator.DoesNotMatch) ? Expression.Not(match) : match;
        }

        if (op is Operator.Equals) { return Expression.Equal(one, other); }
        if (op is Operator.NotEqual) { return Expression.NotEqual(one, other); }

        var compare = Expression.Call(StringMethods.Compare, one, other);

        return op switch
        {
            Operator.LessThan => Expression.LessThan(compare, StringMethods.Zero),
            Operator.LessThanOrEqual => Expression.LessThanOrEqual(compare, StringMethods.Zero),
            Operator.GreaterThan => Expression.GreaterThan(compare, StringMethods.Zero),
            Operator.GreaterThanOrEqual => Expression.GreaterThanOrEqual(compare, StringMethods.Zero),

            _ => null,
        };
    }
}
