using System.Linq.Expressions;
using System.Text.RegularExpressions;

namespace Weequery.Builders;

/// <summary>
/// Puts <see cref="Inquiry{T}.MatchTimeout"/> on the <see cref="Operator.IsMatch"/> calls in an expression, for the
/// paths where the match runs in this process rather than in a database.
/// </summary>
/// <remarks>
/// <para>
/// A condition is built once and may end up either side of that line, so it is built in the shape a provider can
/// translate, which is the two argument <see cref="Regex.IsMatch(string, string)"/>. That overload carries no
/// timeout, and the one that does carries no translation, so the choice cannot be made when the expression is
/// built; it is made where the destination is known and the call swapped for the bounded one.
/// </para>
/// <para>
/// Applied by <see cref="Inquiry{T}.BuildDelegate"/>, which is always in memory, and by
/// <see cref="Inquiry{T}.Build"/> when the query it was given is an in-memory one. A caller compiling
/// <see cref="Inquiry{T}.BuildExpression"/> for itself gets the translatable form, and with it whatever timeout
/// the framework's own default supplies.
/// </para>
/// </remarks>
internal static class RegexTimeout
{
    /// <summary>
    /// The expression with every IsMatch call bounded, or the expression itself when there is nothing to bound.
    /// </summary>
    /// <typeparam name="TClass"></typeparam>
    /// <param name="expression"></param>
    /// <returns></returns>
    internal static Expression<Func<TClass, bool>> Apply<TClass>(Expression<Func<TClass, bool>> expression) where TClass : class
    {
        var timeout = Inquiry<TClass>.MatchTimeout;

        // Nothing to say, and the unbounded overload already means exactly this
        if (timeout == Regex.InfiniteMatchTimeout) { return expression; }

        var bounded = new Visitor(timeout).Visit(expression);

        return (Expression<Func<TClass, bool>>)bounded;
    }

    private sealed class Visitor : ExpressionVisitor
    {
        private readonly Expression Timeout;

        public Visitor(TimeSpan timeout)
        {
            // Structural rather than a caller's value, and this expression is never translated anyway
            Timeout = Expression.Constant(timeout);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method != StringMethods.IsMatch) { return base.VisitMethodCall(node); }

            // The operands are visited too, since either of them can be a comparison of its own
            return Expression.Call(
                StringMethods.IsMatchWithin,
                Visit(node.Arguments[0]),
                Visit(node.Arguments[1]),
                Expression.Constant(RegexOptions.None),
                Timeout);
        }
    }
}
