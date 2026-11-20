using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Weequery.Builders;

namespace Weequery.Bindings;

// The factories. Every binding arrives through one of these, named by a path, by a selector lambda or by a
// constant, and every one leaves through AddTo, see Binding.Use.cs.
internal partial class Binding<TClass>
{
    /// <summary>
    /// A binding for the property a path names
    /// </summary>
    /// <param name="parameter">[OPT] all bindings for the same query should share a common parameter</param>
    /// <param name="propertyPath"></param>
    /// <param name="use">what the binding may be used for, see <see cref="BindingUse"/></param>
    /// <param name="converter">[OPT] the normalisation applied to its values, see <see cref="ValueConverter"/></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException"></exception>
    [RequiresDynamicCode(AotMessages.RuntimeGenerics)]
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    private static Binding<TClass> FromPath(ParameterExpression? parameter, string propertyPath, BindingUse use, ValueConverter? converter)
    {
        WeequeryException.ThrowIfNullOrEmpty(propertyPath);

        var useParameter = parameter ?? Expression.Parameter(typeof(TClass));
        var resolved = GetPropertyExpression(useParameter, propertyPath);

        // The canonical spelling rather than the caller's: a path is matched without regard to case, so one
        // property reached two ways has to arrive as one path or nothing downstream can tell it is one property
        return new Binding<TClass>(useParameter, resolved.Path, resolved.Expression, resolved.ExpressionType, resolved.LinkChecks, isConstant: false, use, converter);
    }

    /// <summary>
    /// A binding for a application supplied constant value
    /// </summary>
    /// <typeparam name="TValue"></typeparam>
    /// <param name="parameter">[OPT] all bindings for the same query should share a common parameter</param>
    /// <param name="key">the name a caller refers to it by</param>
    /// <param name="value"></param>
    /// <param name="use">what the binding may be used for, see <see cref="BindingUse"/></param>
    /// <param name="converter">[OPT] the normalisation applied to its values, see <see cref="ValueConverter"/></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException"></exception>
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    private static Binding<TClass> FromValue<TValue>(ParameterExpression? parameter, string key, TValue value, BindingUse use, ValueConverter? converter)
    {
        WeequeryException.ThrowIfNullOrEmpty(key);
        WeequeryException.ThrowIfNull(value);

        var useParameter = parameter ?? Expression.Parameter(typeof(TClass));

        return new Binding<TClass>(useParameter, key, QueryValue.Of(value), typeof(TValue), [], isConstant: true, use, converter);
    }

    /// <summary>
    /// Create a binding for a constant value rather than a property, optionally adding it to the bindings LUT under the key provided
    /// </summary>
    /// <typeparam name="TValue"></typeparam>
    /// <param name="parameter">[OPT] all bindings for the same query should share a common parameter</param>
    /// <param name="key">the name a caller refers to it by, which a constant has no path to fall back on</param>
    /// <param name="value"></param>
    /// <param name="bindings">[OPT] binding LUT to add to, made by <see cref="BindingLookup.Create"/> so keys are matched the same way everywhere</param>
    /// <param name="use">what the binding may be used for, see <see cref="BindingUse"/></param>
    /// <param name="converter">[OPT] the normalisation applied to its values, see <see cref="ValueConverter"/></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException"></exception>
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    public static Binding<TClass> CreateConstant<TValue>(ParameterExpression? parameter, string key, TValue value, Dictionary<string, Binding<TClass>>? bindings, BindingUse use = BindingUse.All, ValueConverter? converter = null)
    {
        WeequeryException.ThrowIfNullOrEmpty(key);
        WeequeryException.ThrowIfNotBindingKey(key);

        return AddTo(bindings, FromValue(parameter, key, value, use, converter), key);
    }

    /// <summary>
    /// Create binding for the requested property (as indicated by selector), optionally adding it to the bindings LUT (optionally with the given key)
    /// </summary>
    /// <typeparam name="TProperty"></typeparam>
    /// <param name="parameter">[OPT] all bindings for the same query should share a common parameter</param>
    /// <param name="selector">lambda retrieving the parameter of interest (eg. (x)=>x.Name)</param>
    /// <param name="bindings">[OPT] binding LUT to add to, made by <see cref="BindingLookup.Create"/> so keys are matched the same way everywhere</param>
    /// <param name="key">[OPT] key to use to add to LUT, if not provided, .PropertyPath will be used</param>
    /// <returns></returns>
    /// <param name="use">what the binding may be used for, see <see cref="BindingUse"/></param>
    /// <param name="converter">[OPT] the normalisation applied to its values, see <see cref="ValueConverter"/></param>
    /// <exception cref="WeequeryException"></exception>
    [RequiresDynamicCode(AotMessages.RuntimeGenerics)]
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    public static Binding<TClass> Create<TProperty>(ParameterExpression? parameter, Expression<Func<TClass, TProperty>> selector, Dictionary<string, Binding<TClass>>? bindings, string? key = null, BindingUse use = BindingUse.All, ValueConverter? converter = null)
    {
        WeequeryException.ThrowIfNull(selector);
        WeequeryException.ThrowIfNotNullButEmpty(key);
        WeequeryException.ThrowIfNotBindingKey(key);

        var binding = FromPath(parameter, GetPropertyPath(selector), use, converter);

        return AddTo(bindings, binding, key ?? binding.PropertyPath);
    }

    /// <summary>
    /// Create binding for the property the selector reaches
    /// </summary>
    /// <typeparam name="TProperty"></typeparam>
    /// <param name="parameter">[OPT] all bindings for the same query should share a common parameter</param>
    /// <param name="selector">lambda reaching as far as the compiler can follow (eg. (x)=&gt;x.BirthDate)</param>
    /// <param name="segments">the rest of the path, in order (eg. ["Year"])</param>
    /// <param name="bindings">[OPT] binding LUT to add to, made by <see cref="BindingLookup.Create"/> so keys are matched the same way everywhere</param>
    /// <param name="key">[OPT] key to use to add to LUT, if not provided, .PropertyPath will be used</param>
    /// <returns></returns>
    /// <param name="use">what the binding may be used for, see <see cref="BindingUse"/></param>
    /// <param name="converter">[OPT] the normalisation applied to its values, see <see cref="ValueConverter"/></param>
    /// <exception cref="WeequeryException"></exception>
    [RequiresDynamicCode(AotMessages.RuntimeGenerics)]
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    public static Binding<TClass> Create<TProperty>(ParameterExpression? parameter, Expression<Func<TClass, TProperty>> selector, string[] segments, Dictionary<string, Binding<TClass>>? bindings, string? key = null, BindingUse use = BindingUse.All, ValueConverter? converter = null)
    {
        WeequeryException.ThrowIfNull(selector);
        WeequeryException.ThrowIfNullOrEmpty(segments);
        WeequeryException.ThrowIfNotNullButEmpty(key);
        WeequeryException.ThrowIfNotBindingKey(key);

        // Named for the parameter rather than the loop variable, so a path that names nothing reads the same
        // if it was empty, null, or a segment that is
        foreach (var segment in segments) { WeequeryException.ThrowIfNullOrEmpty(segment, nameof(segments)); }

        var binding = FromPath(parameter, JoinSegments(GetPropertyPath(selector), segments), use, converter);

        return AddTo(bindings, binding, key ?? binding.PropertyPath);
    }

    /// <summary>
    /// A binding for the element itself rather than a property of it, for a collection of values such as a
    /// <c>List&lt;string&gt;</c>, keyed <see cref="QuantifiedCondition.ValueKey"/>.
    /// </summary>
    /// <remarks>
    /// The accessor is the parameter, so "Value = 'red'" inside the quantifier reads as <c>t =&gt; t == "red"</c>.
    /// Everything the constructor derives from an accessor follows from that: a string or a Nullable element can
    /// be null and is guarded as any property is, and an int cannot and is not.
    /// </remarks>
    /// <param name="parameter">the element's parameter, shared by every binding for this element type</param>
    /// <param name="bindings">[OPT] binding LUT to add to</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">TClass is not a value a builder can compare, see <see cref="BindingResolver.IsValueElement"/></exception>
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    public static Binding<TClass> CreateElement(ParameterExpression parameter, Dictionary<string, Binding<TClass>>? bindings)
    {
        WeequeryException.ThrowIfNull(parameter);

        if (!BindingResolver.IsValueElement(typeof(TClass)))
        {
            throw new WeequeryException(WeequeryError.BindingInvalid, $"'{QuantifiedCondition.ValueKey}' is the element of a collection of values, such as strings or numbers, and a {typeof(TClass).Name} is not one. Bind the properties of a {typeof(TClass).Name} instead");
        }

        var binding = new Binding<TClass>(parameter, QuantifiedCondition.ValueKey, parameter, typeof(TClass), [], isConstant: false, BindingUse.All, converter: null);

        return AddTo(bindings, binding, QuantifiedCondition.ValueKey);
    }

    /// <summary>
    /// Create binding for the requested property (as specified by path), optionally adding it to the bindings LUT (optionally with the given key)
    /// </summary>
    /// <param name="parameter">[OPT] all bindings for the same query should share a common parameter</param>
    /// <param name="propertyPath">Path to property, for non-nested properties, this will simply be the property name, for nested it will formatted as X.Y.Z</param>
    /// <param name="bindings">[OPT] binding LUT to add to, made by <see cref="BindingLookup.Create"/> so keys are matched the same way everywhere</param>
    /// <param name="key">[OPT] key to use to add to LUT, if not provided, .PropertyPath will be used</param>
    /// <returns></returns>
    /// <param name="use">what the binding may be used for, see <see cref="BindingUse"/></param>
    /// <param name="converter">[OPT] the normalisation applied to its values, see <see cref="ValueConverter"/></param>
    [RequiresDynamicCode(AotMessages.RuntimeGenerics)]
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    public static Binding<TClass> Create(ParameterExpression? parameter, string propertyPath, Dictionary<string, Binding<TClass>>? bindings, string? key = null, BindingUse use = BindingUse.All, ValueConverter? converter = null)
    {
        WeequeryException.ThrowIfNullOrEmpty(propertyPath);
        WeequeryException.ThrowIfNotNullButEmpty(key);
        WeequeryException.ThrowIfNotBindingKey(key);

        var binding = FromPath(parameter, propertyPath, use, converter);

        return AddTo(bindings, binding, key ?? binding.PropertyPath);
    }
}
