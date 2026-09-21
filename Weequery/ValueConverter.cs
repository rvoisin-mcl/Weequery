using System.Linq.Expressions;

namespace Weequery;

/// <summary>
/// A normalisation applied to the values of one binding, so a comparison can be made to agree about things the
/// stored data and the caller spell differently.
/// </summary>
/// <remarks>
/// <para>
/// <code>
/// .BindProperty(minion =&gt; minion.Alias, "Alias",
///     convert: ValueConverter.For&lt;string&gt;(alias =&gt; alias.ToUpper()))
///
/// // "ghost", "Ghost" and "GHOST" now all find the minion stored as "Ghost"
/// .ApplyCondition("Alias = 'ghost'")
/// </code>
/// </para>
/// <para>
/// <b>It runs on whichever sides you say</b>, see <see cref="ConversionTarget"/>: the value the caller wrote, the
/// value in the row, or both. Both is the default and the usual answer, since it is what makes the comparison
/// agree however either side was written.
/// </para>
/// <para>
/// <b>An expression rather than a delegate</b>, because the source side has to become part of the query. A
/// provider turns <c>alias =&gt; alias.ToUpper()</c> into <c>UPPER(alias)</c> and can translate it; it cannot
/// translate a call into your own code, and asking it to will fail when the query runs rather than when the
/// binding is made. For the client side it is compiled and run once, where anything goes.
/// </para>
/// <para>
/// <b>Comparisons only.</b> Sorting and projecting read the stored value whatever this says. An order should be
/// the order of the real data, and a projected row should hand back what is really in it; a caller who wanted
/// the folded value in either place can bind the folded path itself.
/// </para>
/// <para>
/// <b>Null tests are untouched.</b> <see cref="Operator.IsNull"/> and <see cref="Operator.IsNotNull"/> ask
/// if there is a value at all, which no normalisation changes, and the guard every other operator carries
/// is read off the raw property too. So a converter cannot make a null look present, or the reverse, however it
/// is written.
/// </para>
/// <para>
/// One to think twice about: a converter on a string binding also folds the pattern of an
/// <see cref="Operator.IsMatch"/>, and upper casing a regular expression changes what its character classes
/// mean. Set <see cref="ConversionTarget.Binding"/> where the binding is one callers write patterns against.
/// </para>
/// </remarks>
public sealed class ValueConverter
{
    /// <summary>
    /// The type the conversion reads and returns, which must be the binding's unwrapped property type
    /// </summary>
    public Type ValueType { get; }

    /// <summary>
    /// The conversion itself, of the shape <c>TValue -&gt; TValue</c>
    /// </summary>
    public LambdaExpression Conversion { get; }

    /// <summary>Which sides of a comparison it runs against</summary>
    public ConversionTarget Applies { get; }

    /// <summary>
    /// The same conversion compiled, for the client side, where it runs once on a value rather than becoming
    /// part of a query.
    /// </summary>
    /// <remarks>
    /// Over object rather than over the value's own type, so one compiled delegate serves every caller and
    /// nothing has to reach for reflection to invoke it. Built on the first value that needs it and kept, since
    /// a binding outlives the queries that use it.
    /// </remarks>
    private Func<object, object?>? Compiled { get; set; }

    private ValueConverter(Type valueType, LambdaExpression conversion, ConversionTarget applies)
    {
        ValueType = valueType;
        Conversion = conversion;
        Applies = applies;
    }

    /// <summary>
    /// Declare a conversion for values of one type.
    /// </summary>
    /// <remarks>
    /// The type is checked against the binding when the binding is made, so a conversion written for the wrong
    /// one is refused where it is declared rather than where a query using it fails.
    /// </remarks>
    /// <typeparam name="TValue">the binding's unwrapped property type, so int for an int? property</typeparam>
    /// <param name="conversion">must not be null, and must return the type it takes</param>
    /// <param name="applies">[OPT] which sides it runs against, both by default</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">no conversion was given</exception>
    public static ValueConverter For<TValue>(Expression<Func<TValue, TValue>> conversion, ConversionTarget applies = ConversionTarget.Both)
    {
        WeequeryException.ThrowIfNull(conversion);

        return new ValueConverter(typeof(TValue), conversion, applies);
    }

    /// <summary>If it runs against the side described</summary>
    /// <param name="target">one of the flags, not a combination</param>
    /// <returns></returns>
    internal bool Runs(ConversionTarget target)
    {
        return (Applies & target) == target;
    }

    /// <summary>
    /// The conversion applied to an accessor, inlined rather than invoked.
    /// </summary>
    /// <remarks>
    /// The lambda's own parameter is swapped for the accessor and the body used as it stands, which is the shape
    /// a hand written <c>x =&gt; x.Alias.ToUpper()</c> compiles to and the shape a provider reads. Wrapping it in
    /// an <see cref="Expression.Invoke(Expression, Expression[])"/> instead would build the same answer in memory
    /// and refuse to translate.
    /// </remarks>
    /// <param name="accessor">the value to run it on, of <see cref="ValueType"/></param>
    /// <returns></returns>
    internal Expression Inline(Expression accessor)
    {
        return ParameterReplacer.Replace(Conversion.Body, Conversion.Parameters[0], accessor);
    }

    /// <summary>
    /// The conversion run on one value, for the client side.
    /// </summary>
    /// <typeparam name="TValue">must be <see cref="ValueType"/>, which the binding checked</typeparam>
    /// <param name="value"></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">the conversion threw, or gave back nothing to compare against</exception>
    internal TValue Convert<TValue>(TValue value)
    {
        return (TValue)ConvertBoxed(value!);
    }

    /// <summary>
    /// The conversion run on one value of unknown type, for the operand path, where the value has already been
    /// parsed and boxed.
    /// </summary>
    /// <param name="value"></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">the conversion threw, or gave back nothing to compare against</exception>
    internal object ConvertBoxed(object value)
    {
        // Built over object so one delegate serves both entry points, with the casts the value's own type needs
        // wrapped around the same inlined body the source side uses. Compiling twice for two shapes would be two
        // chances for them to disagree.
        Compiled ??= Compile();

        object? converted;

        try
        {
            converted = Compiled(value);
        }
        catch (Exception ex)
        {
            throw new WeequeryException(WeequeryError.ConversionFailed, $"The converter for a {ValueType.Name} failed on '{value}': {ex.Message}", ex);
        }

        // A comparison needs something on its right, and the guard every operator carries is about the property
        // rather than the value, so a null here would build a test nothing satisfies for a reason nobody can see
        return converted ?? throw new WeequeryException(WeequeryError.ConversionFailed, $"The converter for a {ValueType.Name} turned '{value}' into nothing, and a comparison needs a value");
    }

    private Func<object, object?> Compile()
    {
        var boxed = Expression.Parameter(typeof(object), "value");

        var body = Expression.Convert(Inline(Expression.Convert(boxed, ValueType)), typeof(object));

        return Expression.Lambda<Func<object, object?>>(body, boxed).Compile();
    }

    /// <summary>
    /// Swaps one parameter for an expression, which is how a lambda is inlined into another tree.
    /// </summary>
    private sealed class ParameterReplacer : ExpressionVisitor
    {
        private readonly ParameterExpression Parameter;
        private readonly Expression Replacement;

        private ParameterReplacer(ParameterExpression parameter, Expression replacement)
        {
            Parameter = parameter;
            Replacement = replacement;
        }

        internal static Expression Replace(Expression body, ParameterExpression parameter, Expression replacement)
        {
            return new ParameterReplacer(parameter, replacement).Visit(body);
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            return ReferenceEquals(node, Parameter) ? Replacement : base.VisitParameter(node);
        }
    }
}
