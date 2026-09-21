using System.Linq.Expressions;
using System.Reflection;

namespace Weequery.Builders;

/// <summary>
/// Replaces every read of a <see cref="ValueBox{T}"/> with the value it holds, for the paths that compile an
/// expression here rather than hand it to a provider.
/// </summary>
/// <remarks>
/// <para>
/// The box exists so that EF Core promotes a caller's value to a query parameter instead of writing it into the
/// SQL as a literal, which is what gives one query plan for every value rather than one per value, see
/// <see cref="QueryValue"/>. That is worth what it costs — but it only buys anything where there is a provider to
/// read it. Compiled and run here, the indirection is dead weight: the field is read once per row for a value
/// that cannot change, and the compiler pays for a closure field access where a literal would do.
/// </para>
/// <para>
/// <b>It costs more than it looks.</b> Compiling is not priced by node count. Measured over a three term
/// condition, going through boxes rather than literals cost about a third again in compile time, which is most
/// of the distance between an expression this library builds and the one a C# lambda compiles to. The per row
/// cost is smaller and real. Both of them are paid on a path that gains nothing in return.
/// </para>
/// <para>
/// The rewrite is safe because <see cref="ValueBox{T}.Value"/> is readonly and set when the expression is built,
/// so the constant put in its place is the value that read would have produced, every time. Applied by
/// <see cref="Inquiry{T}.BuildDelegate"/> and nowhere else: <see cref="Inquiry{T}.Build"/> hands its expression
/// to whatever provider it was given, and that one must keep its boxes.
/// </para>
/// </remarks>
internal static class ValueInliner
{
    /// <summary>
    /// The expression with every boxed value read as a constant, or the expression itself where it holds none.
    /// </summary>
    /// <typeparam name="TClass"></typeparam>
    /// <param name="expression"></param>
    /// <returns></returns>
    internal static Expression<Func<TClass, bool>> Apply<TClass>(Expression<Func<TClass, bool>> expression) where TClass : class
    {
        return (Expression<Func<TClass, bool>>)new Visitor().Visit(expression);
    }

    private sealed class Visitor : ExpressionVisitor
    {
        protected override Expression VisitMember(MemberExpression node)
        {
            // Field(Constant(box), "Value"), which is the one shape QueryValue builds. A member access that is
            // not that is left alone, including every read of a property off the entity, which is the whole
            // point of the expression and is not evaluatable here at all.
            if ((node.Member is FieldInfo field)
                && (node.Expression is ConstantExpression box)
                && (box.Value is not null)
                && IsValueBox(box.Type)
                && (field.DeclaringType == box.Type))
            {
                // Typed as the field is rather than as the value is, so a null value still constructs and an
                // enum does not arrive as its underlying type
                return Expression.Constant(field.GetValue(box.Value), node.Type);
            }

            return base.VisitMember(node);
        }

        private static bool IsValueBox(Type type)
        {
            return type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(ValueBox<>));
        }
    }
}
