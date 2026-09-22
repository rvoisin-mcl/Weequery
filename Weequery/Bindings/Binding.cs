using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Weequery.Builders;
using Weequery.Interfaces;

namespace Weequery.Bindings;

internal partial class Binding<TClass> : IValueBinding
{
    public string PropertyPath { get; init; }
    public Expression Accessor { get; init; }
    public Type PropertyType { get; init; }
    public bool AccessorIsNullable { get; init; }
    public bool PropertyIsWrappedByNullable { get; init; }

    public Expression UnwrappedAccessor { get; init; }

    /// <summary>
    /// A check per link the path passed through on its way to the property, outermost first: HasValue for a
    /// Nullable, as "BirthDate.Year" against a "DateTime? BirthDate" needs, and not-null for a reference,
    /// as "Lair.Capacity" against a lair that may be missing needs. Empty for a path of one segment.
    /// </summary>
    private List<Expression> LinkChecks { get; init; } = new();

    /// <summary>
    /// If the property can be put in order, so if it can be sorted on.
    /// <para>
    /// Based on the underlying type, since a Nullable does not implement IComparable but its comparer orders it as expected
    /// </para>
    /// </summary>
    public bool IsOrderable { get; init; }

    /// <summary>
    /// If anything about this binding can be null: the property itself, or a link the path passed through
    /// </summary>
    public bool RequiresNullCheck { get { return AccessorIsNullable || (LinkChecks.Count > 0); } }

    /// <summary>
    /// True when the property, and every link on path has a value.
    /// </summary>
    public Expression NotNullCheck { get; init; }

    /// <summary>
    /// If the path passes through anything that could be missing, so if reading the accessor is safe on
    /// its own.
    /// </summary>
    public bool RequiresLinkCheck { get { return LinkChecks.Count > 0; } }

    /// <summary>
    /// True when every link on the way in has a value
    /// </summary>
    public Expression LinkNotNullCheck { get; init; }

    public bool UnwrappedPropertyTypeIsEnum { get; init; }
    public Type UnwrappedPropertyType { get; init; }
    public ParameterExpression Parameter { get; init; }

    /// <summary>
    /// The guard, from the parts of the binding that decide it
    /// </summary>
    /// <param name="accessor"></param>
    /// <param name="accessorType">the accessor's own type, so still wrapped if the property is a Nullable</param>
    /// <param name="wrapped"></param>
    /// <param name="linkChecks"></param>
    /// <returns></returns>
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    private static Expression BuildNotNullCheck(Expression accessor, Type accessorType, bool wrapped, List<Expression> linkChecks)
    {
        List<Expression> checks = new();

        // Every link on the way in, outermost first, so the short circuit protects the steps that follow it
        checks.AddRange(linkChecks);

        // Then the property itself, however its nullness is spelled
        if (wrapped)
        {
            checks.Add(Expression.Property(accessor, "HasValue"));
        }
        else
        {
            if (!accessorType.IsValueType)
            {
                checks.Add(Expression.NotEqual(accessor, Expression.Constant(null, accessorType)));
            }
        }

        return (checks.Count == 0) ? Expression.Constant(true) : checks.Aggregate(Expression.AndAlso);
    }

    /// <summary>
    /// If this binding is a supplied constant or a property
    /// </summary>
    public bool IsConstant { get; init; }

    /// <summary>
    /// What this binding may be used for: any combination of filtering, sorting, or being read back, see
    /// <see cref="BindingUse"/>. All three unless the binding specified otherwise.
    /// </summary>
    public BindingUse Use { get; private set; }

    /// <summary>
    /// Test if this binding can be used the way requested
    /// </summary>
    /// <param name="use">a singular flag, not a combination</param>
    /// <returns></returns>
    public bool Allows(BindingUse use)
    {
        return (Use & use) == use;
    }

    /// <summary>
    /// The optional normalisation applied to this binding's values see <see cref="ValueConverter"/>.
    /// </summary>
    /// <remarks>
    /// Source half of this is folded into <see cref="UnwrappedAccessor"/>
    /// </remarks>
    public ValueConverter? Converter { get; init; }

    /// <summary>
    /// The converter that reached <see cref="UnwrappedAccessor"/>, which is the one a comparison against another
    /// bound property has to agree with.
    /// </summary>
    /// <remarks>
    /// Null where there is no converter, and null where the one there is runs only against a caller's value and
    /// so left the accessor as it found it: such a binding compares as an unconverted one does, because against
    /// another property there is no caller's value for it to run on.
    /// </remarks>
    internal ValueConverter? AccessorConverter => ((Converter is not null) && Converter.Runs(ConversionTarget.Binding)) ? Converter : null;

    /// <summary>
    /// Normalize a supplied value if appropriate, return it unchanged if not
    /// </summary>
    /// <typeparam name="TValue">the unwrapped property type</typeparam>
    /// <param name="value"></param>
    /// <returns></returns>
    public TValue ConvertClientValue<TValue>(TValue value)
    {
        return ((Converter is null) || (!Converter.Runs(ConversionTarget.Value))) ? value : Converter.Convert(value);
    }

    /// <summary>
    /// ctor. Both kinds of binding come through here, so what is derived from an accessor is derived once.
    /// </summary>
    /// <param name="parameter">the "x" the accessor hangs off, shared by every binding used together</param>
    /// <param name="name">the property path, or the key a constant was given, whichever this is</param>
    /// <param name="accessor"></param>
    /// <param name="accessorType">the accessor's own type, so still wrapped if it is a Nullable</param>
    /// <param name="linkChecks">what has to have a value for the accessor to be safe to read</param>
    /// <param name="isConstant"></param>
    /// <param name="use">what the binding may be used for, see <see cref="BindingUse"/></param>
    /// <param name="converter">[OPT] the normalisation applied to its values, see <see cref="ValueConverter"/></param>
    /// <exception cref="WeequeryException"></exception>
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    private Binding(ParameterExpression parameter, string name, Expression accessor, Type accessorType, List<Expression> linkChecks, bool isConstant, BindingUse use, ValueConverter? converter)
    {
        WeequeryException.ThrowIfNullOrEmpty(name);

        Parameter = parameter;
        IsConstant = isConstant;
        Use = use;

        Accessor = accessor;
        PropertyType = accessorType;
        LinkChecks = linkChecks;
        PropertyIsWrappedByNullable = ((accessorType.IsGenericType) && (accessorType.GetGenericTypeDefinition() == typeof(Nullable<>)));

        // A member reached through a nullable is itself nullable, even when its own type is not: BirthDate.Year is an int,
        // but it has no value at all when BirthDate is null, so IsNull applies to it
        AccessorIsNullable = ((!accessorType.IsValueType) || PropertyIsWrappedByNullable || (linkChecks.Count > 0));
        UnwrappedPropertyType = ((PropertyIsWrappedByNullable) ? Nullable.GetUnderlyingType(PropertyType) : PropertyType) ?? throw new WeequeryException(WeequeryError.Internal, "(Should be impossible) Could not determine unwrapped type"); // ex is to eat warning
        UnwrappedPropertyTypeIsEnum = UnwrappedPropertyType.IsEnum;

        IsOrderable = CanBeOrdered(UnwrappedPropertyType);

        // The two trees every operator is built from, settled here rather than rebuilt on each read
        UnwrappedAccessor = PropertyIsWrappedByNullable ? Expression.Property(Accessor, "Value") : Accessor;

        // If a normalization was requested, the source half of a lives here, which limits it to comparisons,
        // sorting and projection will see the unnormalized value
        if (converter is not null)
        {
            if (converter.ValueType != UnwrappedPropertyType)
            {
                throw new WeequeryException(WeequeryError.ConversionFailed, $"The converter for '{name}' is for a {converter.ValueType.Name}, but the property is a {UnwrappedPropertyType.Name}. A converter must be for the unwrapped type (eg. ValueConverter<int> for an int?)");
            }

            Converter = converter;

            if (converter.Runs(ConversionTarget.Binding)) { UnwrappedAccessor = converter.Inline(UnwrappedAccessor); }
        }
        NotNullCheck = BuildNotNullCheck(Accessor, PropertyType, PropertyIsWrappedByNullable, LinkChecks);
        LinkNotNullCheck = (LinkChecks.Count == 0) ? Expression.Constant(true) : LinkChecks.Aggregate(Expression.AndAlso);

        // Check before a collection is potentially squashed to object below
        Index = isConstant ? null : IndexingFor(PropertyType);

        // If the property type is not something that is supported by a builder type, treat it as an object, which will at least support IsNull
        if (!ExpressionBuilder.HasBuilderForBinding(this))
        {
            if (UnwrappedPropertyType.IsValueType) { throw new WeequeryException(WeequeryError.BindingInvalid, $"Could not generate Binding for '{name}', property type {UnwrappedPropertyType.Name} is unsupported"); }

            UnwrappedPropertyType = typeof(object);
        }

        PropertyPath = name;
    }

    /// <summary>
    /// If values of the type can be ordered
    /// </summary>
    /// <param name="type"></param>
    /// <returns></returns>
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    private static bool CanBeOrdered(Type type)
    {
        if (typeof(IComparable).IsAssignableFrom(type)) { return true; }

        return type.GetInterfaces().Any(candidate => candidate.IsGenericType && (candidate.GetGenericTypeDefinition() == typeof(IComparable<>)));
    }

}
