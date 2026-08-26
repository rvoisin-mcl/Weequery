using System.Collections.Concurrent;
using System.Linq.Expressions;

namespace Weequery.Builders;

/// <summary>
/// Holds a single condition value so it can be reached through a field access rather than sitting in the
/// expression tree as a constant. Deliberately mirrors the shape of a compiler generated closure class:
/// a public field on a non-public class, which is exactly what <c>x =&gt; x.Prop == local</c> compiles to.
/// </summary>
/// <typeparam name="T"></typeparam>
internal sealed class ValueBox<T>
{
    public readonly T Value;

    public ValueBox(T value)
    {
        Value = value;
    }
}

internal static class QueryValue
{
    /// <summary>
    /// Wrap a caller supplied value so that EF Core turns it into a query parameter instead of inlining it as a
    /// SQL literal.
    /// <para>
    /// EF Core's parameter extraction promotes any evaluatable subtree that is not itself a
    /// <see cref="ConstantExpression"/> into a parameter, and leaves bare constants as literals. Passing values
    /// as <c>Expression.Constant</c> therefore produced a different SQL string, and so a different query plan,
    /// for every distinct filter value. Reading the value out of a box gives one plan for all of them.
    /// </para>
    /// <para>
    /// Only values that came from the caller should go through here. Structural constants that decide the shape
    /// of the expression, such as the null in an IsNull test or the true/false of an empty conjunction, must stay
    /// as constants so the provider can fold them into IS NULL and the like.
    /// </para>
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="value"></param>
    /// <returns>an expression of type T that reads the value</returns>
    public static Expression Of<T>(T value)
    {
        return Expression.Field(Expression.Constant(new ValueBox<T>(value)), nameof(ValueBox<T>.Value));
    }

    /// <summary>
    /// One box type per value type, kept for the life of the process. MakeGenericType is the expensive part of
    /// <see cref="OfType"/>, and there are only ever as many of these as there are bound property types.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, Type> BoxTypes = new();

    /// <summary>
    /// <inheritdoc cref="Of"/>
    /// <para>
    /// The same thing for a type only known at run time, which is what a value parsed out of a text query is: the
    /// box is still closed over the value's own type, so the expression reads a field of that type rather than an
    /// object that has to be cast back.
    /// </para>
    /// </summary>
    /// <param name="type">the type to hold the value as, which is the bound property's</param>
    /// <param name="value">must be an instance of that type</param>
    /// <returns>an expression of the given type that reads the value</returns>
    public static Expression OfType(Type type, object value)
    {
        var boxType = BoxTypes.GetOrAdd(type, static forType => typeof(ValueBox<>).MakeGenericType(forType));

        var box = Activator.CreateInstance(boxType, value)
            ?? throw new WeequeryException($"(Should be impossible) Could not hold a {type.Name} value");

        return Expression.Field(Expression.Constant(box, boxType), nameof(ValueBox<object>.Value));
    }
}
