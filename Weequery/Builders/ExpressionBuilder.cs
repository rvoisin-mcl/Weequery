using System.Collections.Concurrent;
using System.Linq.Expressions;
using Weequery.Interfaces;

namespace Weequery.Builders;

internal static class ExpressionBuilder
{
    internal static readonly HashSet<Type> SupportedTypes = new()
    {
        typeof(bool),
        typeof(float),
        typeof(double),
        typeof(decimal),
        typeof(byte),
        typeof(sbyte),
        typeof(char),
        typeof(short),
        typeof(ushort),
        typeof(int),
        typeof(uint),
        typeof(long),
        typeof(ulong),
        typeof(DateTime),
        typeof(DateTimeOffset),
        typeof(DateOnly),
        typeof(TimeOnly),
        typeof(TimeSpan),
        typeof(Guid),
        typeof(string),
        typeof(object),
    };

    internal static bool HasBuilderForBinding(IBinding binding)
    {
        if (binding is null) return false;

        var useType = (binding.UnwrappedPropertyTypeIsEnum) ? Enum.GetUnderlyingType(binding.UnwrappedPropertyType) : binding.UnwrappedPropertyType;
        return (SupportedTypes.Contains(useType));
    }

    /// <summary>
    /// One builder per property type, kept for the life of the process.
    /// <para>
    /// The builders hold no state of their own every method takes the binding and the condition it is working
    /// on so a single instance serves every query, and sharing one across threads needs nothing but a
    /// dictionary that is safe to read while another thread adds to it. Worth caching because the alternative was
    /// a new builder for every condition in every query, and for the value types that meant a MakeGenericType and
    /// an Activator.CreateInstance each time, which is the expensive way to arrive at an object with no fields.
    /// </para>
    /// <para>
    /// Keyed on the unwrapped property type, which is what decides the builder: Nullable&lt;&gt; is already
    /// stepped through by then, and an enum keeps its own type rather than collapsing to its underlying one, since
    /// that is what the builder closes over.
    /// </para>
    /// </summary>
    private static readonly ConcurrentDictionary<Type, IExpressionBuilder> Builders = new();

    private static IExpressionBuilder? GetBuilderForBinding(IBinding binding)
    {
        var check = HasBuilderForBinding(binding);
        if (!check) { return null; }

        return Builders.GetOrAdd(binding.UnwrappedPropertyType, CreateBuilder);
    }

    /// <summary>
    /// Called once per property type, on the first condition that needs it. Two threads racing on the same new
    /// type may both run this, but only one instance is kept and either will do.
    /// </summary>
    /// <param name="propertyType">an unwrapped property type that <see cref="HasBuilderForBinding"/> accepted</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException"></exception>
    private static IExpressionBuilder CreateBuilder(Type propertyType)
    {
        if (propertyType == typeof(bool))
        {
            return new BoolExpressionBuilder();
        }

        if (propertyType == typeof(string))
        {
            return new StringExpressionBuilder();
        }

        if (propertyType == typeof(object))
        {
            return new ObjectExpressionBuilder();
        }

        Type builderType = typeof(ValueExpressionBuilder<>).MakeGenericType(propertyType);

        // Never null for a type the supported set vouched for, and returning null here would cache the nothing
        return (IExpressionBuilder?)Activator.CreateInstance(builderType)
            ?? throw new WeequeryException($"(Should be impossible) Could not create a builder for {propertyType.Name}");
    }

    /// <summary>
    /// All bindings for one query share a single parameter expression. Conditions that reference no binding at all
    /// still need that parameter, so their lambda can be combined with its siblings.
    /// </summary>
    private static ParameterExpression SharedParameter<TClass>(Dictionary<string, Binding<TClass>> bindings)
    {
        return (bindings.Count > 0) ? bindings.Values.First().Parameter : Expression.Parameter(typeof(TClass));
    }

    /// <summary>
    /// Build the predicate for a condition tree.
    /// </summary>
    /// <typeparam name="TClass"></typeparam>
    /// <param name="bindings"></param>
    /// <param name="condition"></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">
    /// a field is unbound, an operator does not apply to the property it names, or the tree nests deeper than
    /// <see cref="ConditionNesting.MaxDepth"/>
    /// </exception>
    internal static Expression<Func<TClass, bool>> BuildExpression<TClass>(Dictionary<string, Binding<TClass>> bindings, ICondition condition)
    {
        return BuildExpression(bindings, condition, 0);
    }

    /// <summary>
    /// Build one level of the tree. A condition usually arrives from a caller, so the depth is carried down and
    /// checked, see <see cref="ConditionNesting"/>.
    /// </summary>
    /// <typeparam name="TClass"></typeparam>
    /// <param name="bindings"></param>
    /// <param name="condition"></param>
    /// <param name="depth">levels of nesting already stepped into on the way here</param>
    private static Expression<Func<TClass, bool>> BuildExpression<TClass>(Dictionary<string, Binding<TClass>> bindings, ICondition condition, int depth)
    {
        WeequeryException.ThrowIfNull(condition);

        if (condition is PackedCondition packedCondition)
        {
            return BuildExpression(bindings, packedCondition.Unpack(), depth);
        }

        if (condition is IBound binding)
        {
            if (!bindings.TryGetValue(binding.Field, out var boundProperty))
            {
                throw new WeequeryException($"Unbound field: '{binding.Field}'");
            }

            if (condition is IBoundCondition valueCondition)
            {
                try
                {
                    // Strings only, and refused before the value is read: a pattern is not a number or a date, so
                    // reading it against the property's type would report that it will not parse rather than that
                    // the operator does not belong on this property
                    if ((valueCondition.Operator is Operator.IsMatch or Operator.DoesNotMatch) && (boundProperty.UnwrappedPropertyType != typeof(string)))
                    {
                        throw new WeequeryException($"Operator {valueCondition.Operator} is unsupported for the {boundProperty.UnwrappedPropertyType.Name} binding '{boundProperty.PropertyPath}', it matches a regular expression against text");
                    }

                    // Reading the operands also checks them, so what either route below is handed has already been
                    // vetted: the lists a condition holds are public, and one can have changed since it was built
                    var operands = valueCondition.StringifyOperands();

                    // A comparison naming another property is handed the lookup, since it needs more than the one
                    // binding the builders are chosen by and only ever see
                    if (operands.Any(operand => operand.NamesProperty))
                    {
                        return FieldComparison.Build(boundProperty, valueCondition, operands, bindings);
                    }

                    IExpressionBuilder? builder = GetBuilderForBinding(boundProperty);
                    if (builder is null)
                    {
                        throw new WeequeryException($"No expression builder available for: '{boundProperty.UnwrappedPropertyType.Name}'");
                    }

                    // If conditions are already typed, use as-is, otherwise, resolve
                    return builder.BuildExpression(boundProperty, valueCondition);
                }
                catch (WeequeryException)
                {
                    throw; // Already names the operator, the field or the value it could not do anything with
                }
                catch (Exception ex)
                {
                    // Anything else came from constructing the expression, which is not something a caller can
                    // read: "the binary operator GreaterThan is not defined for the types 'Rank' and 'Rank'" is
                    // true and unhelpful. Say which operator, on which field, of which type, and keep the cause.
                    // Both routes go through here, so a comparison against a property cannot leak one either.
                    throw new WeequeryException($"Operator {valueCondition.Operator} could not be built for field '{valueCondition.Field}' of type {boundProperty.UnwrappedPropertyType.Name}: {ex.Message}", ex);
                }
            }

            throw new WeequeryException($"Unhandled {condition.GetType()} path for expression builder available for: '{boundProperty.UnwrappedPropertyType.Name}'");
        }

        if (condition is IConjunctionCondition compositionCondition)
        {
            var nested = ConditionNesting.Descend(depth);

            // Materialize, otherwise Count()/First()/the aggregate below each rebuild every subtree from scratch
            var expressions = (from component in compositionCondition.Conditions select BuildExpression(bindings, component, nested)).ToList();
            switch (expressions.Count)
            {
                case 0:
                    // No operands, so fall back to the operator's identity: AND over nothing matches everything,
                    // OR over nothing matches nothing. Must carry the shared parameter so this composes when nested.
                    return Expression.Lambda<Func<TClass, bool>>(Expression.Constant(compositionCondition.Operator == Operator.And), SharedParameter(bindings));

                case 1:
                    return expressions.First();

                default:
                    // parameters must be common to aggregate. All bindings should be sharing the same parameter object, so this is basically a NOP
                    var expressionParams = expressions.First().Parameters;
                    var downcast = from expression in expressions select expression.Body;

                    switch (compositionCondition.Operator)
                    {
                        case Operator.Or:
                            return Expression.Lambda<Func<TClass, bool>>(downcast.Aggregate(Expression.OrElse), expressionParams);

                        case Operator.And:
                            return Expression.Lambda<Func<TClass, bool>>(downcast.Aggregate(Expression.AndAlso), expressionParams);

                        default:
                            throw new WeequeryException($"Unsupported operator type: '{compositionCondition.Operator}'");
                    }
            }
        }

        if (condition is INotCondition notCondition)
        {
            if (notCondition.Conditions.Count == 0) { throw new WeequeryException($"{nameof(INotCondition)} has no condition to negate"); }

            // Negate the *body* and carry the operand's parameter through, Expression.Not cannot be applied to the lambda itself
            var operand = BuildExpression(bindings, notCondition.Conditions.First(), ConditionNesting.Descend(depth));
            return Expression.Lambda<Func<TClass, bool>>(Expression.Not(operand.Body), operand.Parameters);
        }

        throw new WeequeryException($"Unsupported condition type: '{condition.GetType().Name}'");
    }
}