using System.Linq.Expressions;
using Weequery.Interfaces;

namespace Weequery.Builders;

/// <summary>
/// Provide string-ly typed expression building, also a unspecialized interface for
/// <see cref="IExpressionBuilder{T}"/> we can store in a LUT
/// </summary>
internal interface IExpressionBuilder
{
    /// <summary>
    /// Build the expression for a condition of whatever type it arrived in.
    /// </summary>
    /// <typeparam name="TClass"></typeparam>
    /// <param name="binding"></param>
    /// <param name="condition"></param>
    /// <returns></returns>
    Expression<Func<TClass, bool>> BuildExpression<TClass>(Binding<TClass> binding, IBoundCondition condition);

    /// <summary>
    /// Expected to cast condition to appropriate type for binding and call <see cref="IExpressionBuilder{T}"/>
    /// </summary>
    /// <typeparam name="TClass"></typeparam>
    /// <param name="binding"></param>
    /// <param name="condition"></param>
    /// <returns></returns>
    Expression<Func<TClass, bool>> BuildTypedExpressionFromStringifiedCondition<TClass>(Binding<TClass> binding, TypedCondition<string> condition);
}
