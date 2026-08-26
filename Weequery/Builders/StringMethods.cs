using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Weequery.Builders;

/// <summary>
/// The string methods an expression is built from, resolved once. Seperated so the per type builder and the
/// comparison against another property can share them
/// </summary>
/// <remarks>
/// <para>
/// Notes:
/// String comparison will differ between in-memory and SQL, in-memory will use the current culture, 
/// SQL behaviour will depend on the column.
/// EF Core cannot translate StringComparison, do not use as a replacement
/// </para>
/// </remarks>
internal static class StringMethods
{
    /// <summary>string.StartsWith(string)</summary>
    internal static readonly MethodInfo StartsWith = typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!;

    /// <summary>string.EndsWith(string)</summary>
    internal static readonly MethodInfo EndsWith = typeof(string).GetMethod(nameof(string.EndsWith), [typeof(string)])!;

    /// <summary>string.Contains(string)</summary>
    internal static readonly MethodInfo Contains = typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;

    /// <summary>
    /// The two argument static Compare, which is how a string is ordered: the type has no &lt; or &gt; of its own,
    /// so building one the way the value types are built reports that the operator is not defined for it. Compared
    /// against zero this is the form EF Core turns into the SQL operator, so the collation decides the order the
    /// same way it decides the substring matches.
    /// </summary>
    internal static readonly MethodInfo Compare = typeof(string).GetMethod(nameof(string.Compare), [typeof(string), typeof(string)])!;

    /// <summary>
    /// The two argument static Regex.IsMatch, which is the only shape of it a provider translates: SQLite reads
    /// it as REGEXP and PostgreSQL as '~', see <see cref="Operator.IsMatch"/>.
    /// <para>
    /// Static rather than an instance method on a compiled Regex, and without the overloads taking
    /// <see cref="RegexOptions"/> or a timeout, because none of those are translated. Where the match runs here
    /// rather than in a database the call is swapped for the one that takes a timeout, see
    /// <see cref="RegexTimeout"/>.
    /// </para>
    /// </summary>
    internal static readonly MethodInfo IsMatch = typeof(Regex).GetMethod(nameof(Regex.IsMatch), [typeof(string), typeof(string)])!;

    /// <summary>
    /// The same match, bounded. Not translatable, so it only ever appears in an expression that has already been
    /// settled as running in this process.
    /// </summary>
    internal static readonly MethodInfo IsMatchWithin = typeof(Regex).GetMethod(nameof(Regex.IsMatch), [typeof(string), typeof(string), typeof(RegexOptions), typeof(TimeSpan)])!;

    /// <summary>
    /// What a comparison is measured against. A structural constant rather than a caller's value, so it stays a
    /// constant, see <see cref="QueryValue"/>.
    /// </summary>
    internal static readonly Expression Zero = Expression.Constant(0);

    /// <summary>
    /// The method for one of the substring operators, or null for anything else
    /// </summary>
    /// <param name="op"></param>
    /// <returns></returns>
    internal static MethodInfo? Substring(Operator op)
    {
        return op switch
        {
            Operator.StartsWith or Operator.DoesNotStartWith => StartsWith,
            Operator.EndsWith or Operator.DoesNotEndWith => EndsWith,
            Operator.Contains or Operator.DoesNotContain => Contains,

            _ => null,
        };
    }

    /// <summary>
    /// Whether the operator is the negative of its pair, so whether the call it is built from has to be negated
    /// </summary>
    /// <param name="op"></param>
    /// <returns></returns>
    internal static bool IsNegated(Operator op)
    {
        return op is Operator.DoesNotStartWith or Operator.DoesNotEndWith or Operator.DoesNotContain;
    }
}
