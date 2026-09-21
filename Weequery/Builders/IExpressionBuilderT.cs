using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Weequery.Bindings;

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
    [RequiresDynamicCode(AotMessages.RuntimeGenerics)]
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    Expression<Func<TClass, bool>> BuildTypedExpressionFromTypedCondition<TClass>(Binding<TClass> binding, TypedCondition<T> condition);
}