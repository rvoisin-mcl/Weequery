using System.Linq.Expressions;
using Weequery.Interfaces;

namespace Weequery.Builders;

/// <summary>
/// The part every builder does the same way: choosing which of its two routes a condition takes.
/// </summary>
/// <remarks>
/// A condition off the wire will be string-ly typed and will have to be resolved to the binding type, a code-first condition
/// will already holds values of the appropriate type. The builder is chosen by the binding's type, so
/// a condition of that type needs no conversion at all.
/// </remarks>
/// <typeparam name="T">the type this builder builds for, which is the binding's unwrapped property type</typeparam>
internal abstract class ExpressionBuilderBase<T> : IExpressionBuilder<T>
{
    /// <summary>
    /// <inheritdoc cref="IExpressionBuilder.BuildExpression"/>
    /// </summary>
    /// <typeparam name="TClass"></typeparam>
    /// <param name="binding"></param>
    /// <param name="condition"></param>
    /// <returns></returns>
    public Expression<Func<TClass, bool>> BuildExpression<TClass>(Binding<TClass> binding, IBoundCondition condition)
    {
        var typed = TypedCondition<T>.From(condition);

        return (typed is not null)
            ? BuildTypedExpressionFromTypedCondition(binding, typed)
            : BuildTypedExpressionFromStringifiedCondition(binding, TypedCondition<T>.FromText(condition));
    }

    /// <summary>
    /// <inheritdoc cref="IExpressionBuilder{T}.BuildTypedExpressionFromTypedCondition"/>
    /// </summary>
    /// <typeparam name="TClass"></typeparam>
    /// <param name="binding"></param>
    /// <param name="condition"></param>
    /// <returns></returns>
    public abstract Expression<Func<TClass, bool>> BuildTypedExpressionFromTypedCondition<TClass>(Binding<TClass> binding, TypedCondition<T> condition);

    /// <summary>
    /// <inheritdoc cref="IExpressionBuilder.BuildTypedExpressionFromStringifiedCondition"/>
    /// </summary>
    /// <typeparam name="TClass"></typeparam>
    /// <param name="binding"></param>
    /// <param name="condition"></param>
    /// <returns></returns>
    public abstract Expression<Func<TClass, bool>> BuildTypedExpressionFromStringifiedCondition<TClass>(Binding<TClass> binding, TypedCondition<string> condition);
}
