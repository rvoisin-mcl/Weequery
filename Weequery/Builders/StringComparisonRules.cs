using System.Linq.Expressions;
using System.Reflection;

namespace Weequery.Builders;

/// <summary>
/// Puts <see cref="InquirySettings.StringComparison"/> on the string comparisons in an expression, for the paths
/// where the comparison runs in this process rather than in a database.
/// </summary>
/// <remarks>
/// <para>
/// The same shape as <see cref="RegexTimeout"/>, and for the same reason. A condition is built once and may end
/// up either side of that line, so it is built from the forms a provider translates: the single argument string
/// methods, the equality operator, and List.Contains. None of those carry a
/// <see cref="System.StringComparison"/>, and the forms that do carry no translation, so the choice cannot be
/// made when the expression is built; it is made where the destination is known and the comparisons swapped for
/// the ones that take it.
/// </para>
/// <para>
/// This is also where the inconsistency <see cref="StringExpressionBuilder"/> documents is settled: the single
/// argument StartsWith and EndsWith compare linguistically while Contains is ordinal, so left alone the three
/// disagree about the same pair of strings. Swapped together they take whatever the query asked for.
/// </para>
/// <para>
/// A null is left alone. The guards a binding carries are equality against a null constant, and what they ask is
/// whether the value is there at all, which no comparison rule has an opinion about.
/// </para>
/// <para>
/// Applied by <see cref="Inquiry{T}.BuildDelegate"/>, which is always in memory, and by
/// <see cref="Inquiry{T}.Build"/> when the query it was given is an in-memory one. A caller compiling
/// <see cref="Inquiry{T}.BuildExpression"/> for itself gets the translatable form, and with it the framework own
/// defaults for each form.
/// </para>
/// </remarks>
internal static class StringComparisonRules
{
    /// <summary>
    /// The expression with every string comparison told how to compare.
    /// </summary>
    /// <typeparam name="TClass"></typeparam>
    /// <param name="expression"></param>
    /// <param name="settings">the settings the query holds, never null</param>
    /// <returns></returns>
    internal static Expression<Func<TClass, bool>> Apply<TClass>(Expression<Func<TClass, bool>> expression, InquirySettings settings) where TClass : class
    {
        var swapped = new Visitor(settings.StringComparison).Visit(expression);

        return (Expression<Func<TClass, bool>>)swapped;
    }

    private sealed class Visitor : ExpressionVisitor
    {
        /// <summary>string.StartsWith(string, StringComparison)</summary>
        private static readonly MethodInfo StartsWithRules = typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string), typeof(StringComparison)])!;

        /// <summary>string.EndsWith(string, StringComparison)</summary>
        private static readonly MethodInfo EndsWithRules = typeof(string).GetMethod(nameof(string.EndsWith), [typeof(string), typeof(StringComparison)])!;

        /// <summary>string.Contains(string, StringComparison)</summary>
        private static readonly MethodInfo ContainsRules = typeof(string).GetMethod(nameof(string.Contains), [typeof(string), typeof(StringComparison)])!;

        /// <summary>string.Compare(string, string, StringComparison)</summary>
        private static readonly MethodInfo CompareRules = typeof(string).GetMethod(nameof(string.Compare), [typeof(string), typeof(string), typeof(StringComparison)])!;

        /// <summary>
        /// string.Equals(string, string, StringComparison). The static one, so that neither side has to be known
        /// to be there: the operator this replaces has no opinion about which of its operands came from the row.
        /// </summary>
        private static readonly MethodInfo EqualsRules = typeof(string).GetMethod(nameof(string.Equals), [typeof(string), typeof(string), typeof(StringComparison)])!;

        /// <summary>List of string Contains, which is what the IsIn family is built from</summary>
        private static readonly MethodInfo ListContains = typeof(List<string>).GetMethod(nameof(List<string>.Contains), [typeof(string)])!;

        /// <summary>The Enumerable one, which is the only shape of it that takes a comparer</summary>
        private static readonly MethodInfo ListContainsRules = typeof(Enumerable)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(candidate => (candidate.Name == nameof(Enumerable.Contains)) && (candidate.GetParameters().Length == 3))
            .MakeGenericMethod(typeof(string));

        private readonly Expression Comparison;

        private readonly Expression Comparer;

        public Visitor(StringComparison comparison)
        {
            // Structural rather than a caller value, and this expression is never translated anyway
            Comparison = Expression.Constant(comparison);
            Comparer = Expression.Constant(StringComparer.FromComparison(comparison), typeof(IEqualityComparer<string>));
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            // The operands are visited either way, since either of them can be a comparison of its own
            if (node.Method == StringMethods.Compare)
            {
                return Expression.Call(CompareRules, Visit(node.Arguments[0]), Visit(node.Arguments[1]), Comparison);
            }

            // The list is the constant the values were put in, so it is the source and the column is what is sought
            if (node.Method == ListContains)
            {
                return Expression.Call(ListContainsRules, Visit(node.Object)!, Visit(node.Arguments[0]), Comparer);
            }

            var substring = Substring(node.Method);
            if (substring is null) { return base.VisitMethodCall(node); }

            return Expression.Call(Visit(node.Object)!, substring, Visit(node.Arguments[0]), Comparison);
        }

        /// <summary>
        /// Equality between two strings, which the expression API builds as an operator rather than as a call and
        /// which therefore compares ordinally however the rest of the condition was told to compare.
        /// </summary>
        protected override Expression VisitBinary(BinaryExpression node)
        {
            if (node.NodeType is not (ExpressionType.Equal or ExpressionType.NotEqual)) { return base.VisitBinary(node); }
            if ((node.Left.Type != typeof(string)) || (node.Right.Type != typeof(string))) { return base.VisitBinary(node); }

            // A null on either side asks whether the value is there rather than what it is, and the guards a
            // binding carries are exactly that. No comparison rule has an opinion about it, so it is left alone.
            if (IsNull(node.Left) || IsNull(node.Right)) { return base.VisitBinary(node); }

            var equal = Expression.Call(EqualsRules, Visit(node.Left), Visit(node.Right), Comparison);

            return (node.NodeType == ExpressionType.Equal) ? equal : Expression.Not(equal);
        }

        private static bool IsNull(Expression expression)
        {
            return (expression is ConstantExpression constant) && (constant.Value is null);
        }

        /// <summary>
        /// The overload taking a comparison for one of the substring calls, or null for anything else
        /// </summary>
        private static MethodInfo? Substring(MethodInfo method)
        {
            if (method == StringMethods.StartsWith) { return StartsWithRules; }
            if (method == StringMethods.EndsWith) { return EndsWithRules; }
            if (method == StringMethods.Contains) { return ContainsRules; }

            return null;
        }
    }
}
