using System.Linq.Expressions;
using Weequery.Bindings;
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
    /// <remarks>
    /// <para>
    /// The one place a client value is normalised, see <see cref="ValueConverter"/>. Both routes into a builder
    /// arrive here a condition that was already typed comes straight in, and one that arrived as text is
    /// parsed and then handed back to it so converting once here converts every value exactly once, whatever
    /// shape the operator holds them in.
    /// </para>
    /// <para>
    /// Sealed for the same reason. A builder overriding it could take a value without the conversion, and the
    /// failure would be a query quietly comparing the wrong thing rather than anything that looks like a bug.
    /// </para>
    /// </remarks>
    /// <typeparam name="TClass"></typeparam>
    /// <param name="binding"></param>
    /// <param name="condition"></param>
    /// <returns></returns>
    public Expression<Func<TClass, bool>> BuildTypedExpressionFromTypedCondition<TClass>(Binding<TClass> binding, TypedCondition<T> condition)
    {
        WeequeryException.ThrowIfNull(binding);
        WeequeryException.ThrowIfNull(condition);

        var converted = (binding.Converter is null) ? condition : condition.Transform(binding.ConvertClientValue);

        return Build(binding, converted);
    }

    /// <summary>
    /// Build the expression for a condition whose values are of this builder's type and already normalised.
    /// </summary>
    /// <typeparam name="TClass"></typeparam>
    /// <param name="binding"></param>
    /// <param name="condition"></param>
    /// <returns></returns>
    protected abstract Expression<Func<TClass, bool>> Build<TClass>(Binding<TClass> binding, TypedCondition<T> condition);

    /// <summary>
    /// <inheritdoc cref="IExpressionBuilder.BuildTypedExpressionFromStringifiedCondition"/>
    /// </summary>
    /// <typeparam name="TClass"></typeparam>
    /// <param name="binding"></param>
    /// <param name="condition"></param>
    /// <returns></returns>
    public abstract Expression<Func<TClass, bool>> BuildTypedExpressionFromStringifiedCondition<TClass>(Binding<TClass> binding, TypedCondition<string> condition);
}
