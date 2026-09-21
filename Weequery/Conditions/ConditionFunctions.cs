using Weequery.Interfaces;
using Weequery.Parsing;

namespace Weequery;

/// <summary>
/// Various helper functions and extensions for Condition classes
/// </summary>
public static partial class ConditionFunctions
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

}
