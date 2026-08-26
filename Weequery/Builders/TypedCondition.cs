using Weequery.Interfaces;

namespace Weequery.Builders;

/// <summary>
/// A comparison reduced to what a builder needs: the operator, the field it names, and its operands as values of
/// one type.
/// </summary>
/// <remarks>
/// <para>
/// The four comparison types hold one, two or many operands, and any of those may name another bound property
/// rather than be a value. A builder is chosen by a single binding's type and never sees more than that binding,
/// so a condition naming another property is routed to <see cref="FieldComparison"/> before it reaches one; what
/// arrives here is therefore always plain values, and there are as many of them as the operator takes.
/// </para>
/// <para>
/// Flattening the shapes to one list means each builder is written once rather than once per shape, which is
/// what the operators want anyway: the same expression is built for a value whether it came from the one that
/// <see cref="Operator.Equals"/> holds or from one of the pair <see cref="Operator.IsBetween"/> does.
/// </para>
/// </remarks>
/// <typeparam name="T">the binding's unwrapped property type, or string on the way to it</typeparam>
internal sealed class TypedCondition<T>
{
    /// <summary>
    /// <inheritdoc cref="ICondition.Operator"/>
    /// </summary>
    public required Operator Operator { get; init; }

    /// <summary>
    /// <inheritdoc cref="IBound.Field"/>
    /// </summary>
    public required string Field { get; init; }

    /// <summary>
    /// What the property is tested against, in the order the condition held them
    /// </summary>
    public required List<T> Values { get; init; }

    /// <summary>
    /// The same condition over another type, which is how a condition that arrived as text is read against the
    /// bound property's type
    /// </summary>
    /// <typeparam name="U"></typeparam>
    /// <param name="transformFunc"></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException"></exception>
    public TypedCondition<U> Transform<U>(Func<T, U> transformFunc)
    {
        if (transformFunc is null) { throw new WeequeryException($"{nameof(transformFunc)} cannot be null"); }

        return new TypedCondition<U>
        {
            Operator = Operator,
            Field = Field,
            Values = [.. from value in Values select transformFunc(value)],
        };
    }

    /// <summary>
    /// Read a condition's operands as values of T, if that is how it holds them.
    /// </summary>
    /// <remarks>
    /// A condition taking no value matches whatever T is asked for, since there is nothing held to be of the
    /// wrong type; that is what lets one builder answer <see cref="Operator.IsNull"/> for its own binding.
    /// </remarks>
    /// <param name="condition"></param>
    /// <returns>null if the condition holds its operands as something other than T</returns>
    public static TypedCondition<T>? From(IBoundCondition condition)
    {
        switch (condition)
        {
            case INoValueCondition:
                return Of(condition, []);

            case IOneValueCondition<T> one:
                return Of(condition, [one.Value.Value]);

            case ITwoValueCondition<T> two:
                return Of(condition, [two.Value1.Value, two.Value2.Value]);

            case IMultipleValueCondition<T> many:
                return Of(condition, [.. from value in many.Values select value.Value]);

            default:
                return null;
        }
    }

    /// <summary>
    /// A condition's operands as the text they travel as, which is what a builder parses against its own type
    /// when the condition arrived over the wire rather than being built in code
    /// </summary>
    /// <param name="condition"></param>
    /// <returns></returns>
    public static TypedCondition<string> FromText(IBoundCondition condition)
    {
        return new TypedCondition<string>
        {
            Operator = condition.Operator,
            Field = condition.Field,
            Values = condition.StringifyValues(),
        };
    }

    private static TypedCondition<T> Of(IBoundCondition condition, List<T> values)
    {
        return new TypedCondition<T> { Operator = condition.Operator, Field = condition.Field, Values = values };
    }
}
