using System.Linq.Expressions;

namespace Weequery.Builders;

/// <summary>
/// Provide strongly typed expression building
/// </summary>
/// <typeparam name="T"></typeparam>
internal interface IExpressionBuilder<T> : IExpressionBuilder
{
    /// <summary>
    /// Build expression for condition
    /// </summary>
    /// <typeparam name="TClass"></typeparam>
    /// <param name="binding"></param>
    /// <param name="condition"></param>
    /// <returns></returns>
    Expression<Func<TClass, bool>> BuildTypedExpressionFromTypedCondition<TClass>(Binding<TClass> binding, TypedCondition<T> condition);
}