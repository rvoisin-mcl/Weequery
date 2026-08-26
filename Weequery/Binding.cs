using System.Linq.Expressions;
using System.Reflection;
using Weequery.Builders;
using Weequery.Interfaces;

namespace Weequery;

internal class Binding<TClass> : IBinding
{
    public string PropertyPath { get; init; }
    public MemberExpression Accessor { get; init; }
    public Type PropertyType { get; init; }
    public bool AccessorIsNullable { get; init; }
    public bool PropertyIsWrappedByNullable { get; init; }

    public Expression UnwrappedAccessor { get; init; }

    /// <summary>
    /// A check per link the path passed through on its way to the property, outermost first: HasValue for a
    /// Nullable&lt;&gt;, as "BirthDate.Year" against a "DateTime? BirthDate" needs, and not-null for a reference,
    /// as "Lair.Capacity" against a lair that may be missing needs. Empty for a path of one segment.
    /// </summary>
    private List<Expression> LinkChecks { get; init; } = new();

    /// <summary>
    /// Whether the property can be put in order, so whether it can be sorted on.
    /// <para>
    /// Asked of the underlying type, since a Nullable&lt;&gt; does not implement IComparable itself even though
    /// its comparer orders it fine.
    /// </para>
    /// </summary>
    public bool IsOrderable { get; init; }

    /// <summary>
    /// Whether anything about this binding can be null: the property itself, or a link the path went through
    /// </summary>
    public bool RequiresNullCheck { get { return AccessorIsNullable || (LinkChecks.Count > 0); } }

    /// <summary>
    /// True when the property, and every link on the way to it, has a value.
    /// </summary>
    public Expression NotNullCheck { get; init; }

    /// <summary>
    /// Whether the path passes through anything that could be missing, so whether reading the accessor is safe on
    /// its own. False for a plain property, however nullable the property itself is: reading a Nullable&lt;&gt;
    /// never fails, it is stepping through one that does.
    /// </summary>
    public bool RequiresLinkCheck { get { return LinkChecks.Count > 0; } }

    /// <summary>
    /// True when every link on the way in has a value, saying nothing about the property at the end of it. What
    /// guards a read of the accessor, as against <see cref="NotNullCheck"/>, which guards a test of its value.
    /// </summary>
    public Expression LinkNotNullCheck { get; init; }

    /// <summary>
    /// The guard, from the parts of the binding that decide it
    /// </summary>
    /// <param name="accessor"></param>
    /// <param name="accessorType">the accessor's own type, so still wrapped if the property is a Nullable&lt;&gt;</param>
    /// <param name="wrapped"></param>
    /// <param name="linkChecks"></param>
    /// <returns></returns>
    private static Expression BuildNotNullCheck(MemberExpression accessor, Type accessorType, bool wrapped, List<Expression> linkChecks)
    {
        List<Expression> checks = new();

        // Every link on the way in, outermost first, so the short circuit protects the step that follows it
        checks.AddRange(linkChecks);

        // Then the property itself, however its nullness is spelled
        if (wrapped) { checks.Add(Expression.Property(accessor, "HasValue")); }
        else if (!accessorType.IsValueType) { checks.Add(Expression.NotEqual(accessor, Expression.Constant(null, accessorType))); }

        return (checks.Count == 0) ? Expression.Constant(true) : checks.Aggregate(Expression.AndAlso);
    }
    public bool UnwrappedPropertyTypeIsEnum { get; init; }
    public Type UnwrappedPropertyType { get; init; }
    public ParameterExpression Parameter { get; init; }

    /// <summary>
    /// If this binding is a supplied constant or a property of the row.
    /// <para>
    /// A constant reads the same way a property does, so it can be the other side of a comparison, but it has no
    /// per-row value: sorting by one would sort by nothing, so it is refused rather than quietly doing nothing.
    /// </para>
    /// </summary>
    public bool IsConstant { get; init; }

    /// <summary>
    /// ctor. Both kinds of binding come through here, so what is derived from an accessor is derived once.
    /// </summary>
    /// <param name="parameter">the "x" the accessor hangs off, shared by every binding used together</param>
    /// <param name="name">the property path, or the key a constant was given, whichever this is</param>
    /// <param name="accessor"></param>
    /// <param name="accessorType">the accessor's own type, so still wrapped if it is a Nullable&lt;&gt;</param>
    /// <param name="linkChecks">what has to have a value for the accessor to be safe to read</param>
    /// <param name="isConstant"></param>
    /// <exception cref="WeequeryException"></exception>
    private Binding(ParameterExpression parameter, string name, MemberExpression accessor, Type accessorType, List<Expression> linkChecks, bool isConstant)
    {
        WeequeryException.ThrowIfNullOrEmpty(name);

        Parameter = parameter;
        IsConstant = isConstant;

        Accessor = accessor;
        PropertyType = accessorType;
        LinkChecks = linkChecks;
        PropertyIsWrappedByNullable = ((accessorType.IsGenericType) && (accessorType.GetGenericTypeDefinition() == typeof(Nullable<>)));

        // A member reached through a nullable is itself nullable, even when its own type is not: BirthDate.Year is
        // an int, but it has no value at all when BirthDate is null, so IsNull applies to it
        AccessorIsNullable = ((!accessorType.IsValueType) || PropertyIsWrappedByNullable || (linkChecks.Count > 0));
        UnwrappedPropertyType = ((PropertyIsWrappedByNullable) ? Nullable.GetUnderlyingType(PropertyType) : PropertyType) ?? throw new WeequeryException("(Should be impossible) Could not determine unwrapped type"); // ex is to eat warning
        UnwrappedPropertyTypeIsEnum = UnwrappedPropertyType.IsEnum;

        IsOrderable = CanBeOrdered(UnwrappedPropertyType);

        // The two trees every operator is built from, settled here rather than rebuilt on each read
        UnwrappedAccessor = PropertyIsWrappedByNullable ? Expression.Property(Accessor, "Value") : Accessor;
        NotNullCheck = BuildNotNullCheck(Accessor, PropertyType, PropertyIsWrappedByNullable, LinkChecks);
        LinkNotNullCheck = (LinkChecks.Count == 0) ? Expression.Constant(true) : LinkChecks.Aggregate(Expression.AndAlso);

        // If the property type is not something that is supported by a builder type, treat it as an object, which will at least support IsNull
        if (!ExpressionBuilder.HasBuilderForBinding(this))
        {
            if (UnwrappedPropertyType.IsValueType) { throw new WeequeryException($"Could not generate Binding for '{name}', property type {UnwrappedPropertyType.Name} is unsupported"); }

            UnwrappedPropertyType = typeof(object);
        }

        PropertyPath = name;
    }

    /// <summary>
    /// A binding for the property a path names
    /// </summary>
    /// <param name="parameter">[OPT] all bindings for the same query should share a common parameter</param>
    /// <param name="propertyPath"></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException"></exception>
    private static Binding<TClass> FromPath(ParameterExpression? parameter, string propertyPath)
    {
        WeequeryException.ThrowIfNullOrEmpty(propertyPath);

        var useParameter = parameter ?? Expression.Parameter(typeof(TClass));
        var resolved = GetPropertyExpression(useParameter, propertyPath);

        return new Binding<TClass>(useParameter, propertyPath, resolved.Expression, resolved.ExpressionType, resolved.LinkChecks, isConstant: false);
    }

    /// <summary>
    /// A binding for a value the application supplied, which reads the same as a property but is the same for
    /// every row.
    /// </summary>
    /// <remarks>
    /// The value is reached through a box, the way a condition's own values are, so a provider passes it as a
    /// parameter rather than writing it into the statement see <see cref="QueryValue"/>. That also makes the
    /// accessor a member access like any other, so everything built from a binding is built the same way.
    /// </remarks>
    /// <typeparam name="TValue"></typeparam>
    /// <param name="parameter">[OPT] all bindings for the same query should share a common parameter</param>
    /// <param name="key">the name a caller refers to it by</param>
    /// <param name="value"></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException"></exception>
    private static Binding<TClass> FromValue<TValue>(ParameterExpression? parameter, string key, TValue value)
    {
        WeequeryException.ThrowIfNullOrEmpty(key);
        WeequeryException.ThrowIfNull(value);

        var useParameter = parameter ?? Expression.Parameter(typeof(TClass));

        return new Binding<TClass>(useParameter, key, (MemberExpression)QueryValue.Of(value), typeof(TValue), [], isConstant: true);
    }

    /// <summary>
    /// Create a binding for a value rather than a property, optionally adding it to the bindings LUT under the key
    /// it was given.
    /// </summary>
    /// <typeparam name="TValue"></typeparam>
    /// <param name="parameter">[OPT] all bindings for the same query should share a common parameter</param>
    /// <param name="key">the name a caller refers to it by, which a constant has no path to fall back on</param>
    /// <param name="value"></param>
    /// <param name="bindings">[OPT] binding LUT to add to, made by <see cref="BindingLookup.Create"/> so keys are matched the same way everywhere</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException"></exception>
    public static Binding<TClass> CreateConstant<TValue>(ParameterExpression? parameter, string key, TValue value, Dictionary<string, Binding<TClass>>? bindings)
    {
        WeequeryException.ThrowIfNullOrEmpty(key);
        WeequeryException.ThrowIfNotBindingKey(key);

        return AddTo(bindings, FromValue(parameter, key, value), key);
    }

    /// <summary>
    /// Whether values of the type can say which of two comes first. Both spellings count: the generic interface
    /// is what the primitives and strings implement, and the old one catches a type that only implements that.
    /// </summary>
    /// <param name="type"></param>
    /// <returns></returns>
    private static bool CanBeOrdered(Type type)
    {
        if (typeof(IComparable).IsAssignableFrom(type)) { return true; }

        return type.GetInterfaces().Any(candidate => candidate.IsGenericType && (candidate.GetGenericTypeDefinition() == typeof(IComparable<>)));
    }

    private static Type GetMemberType(MemberExpression expression)
    {
        switch (expression.Member.MemberType)
        {
            case MemberTypes.Field:
                return ((FieldInfo)expression.Member).FieldType;

            case MemberTypes.Property:
                return ((PropertyInfo)expression.Member).PropertyType;

            case MemberTypes.Event: // would be: ((EventInfo)Accessor.Member).EventHandlerType;
            case MemberTypes.Method: // would be: ((MethodInfo)Accessor.Member).ReturnType;
            default:
                throw new WeequeryException($"Could not generate member for expression {expression}");
        }
    }

    private record GetPropertyExpressionRecord(MemberExpression Expression, Type ExpressionType, List<Expression> LinkChecks);

    private static bool IsNullable(Type type)
    {
        return type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Nullable<>));
    }

    /// <summary>
    /// Whether the type declares the member itself. Matches how <see cref="Expression.PropertyOrField"/> looks one
    /// up, so the two agree on what counts as present.
    /// </summary>
    private static bool HasMember(Type type, string name)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase | BindingFlags.FlattenHierarchy;

        return (type.GetProperty(name, flags) is not null) || (type.GetField(name, flags) is not null);
    }

    /// <summary>
    /// Given a input and an property path, build a expression chain to return value of the final link
    /// <para>
    /// A path may reach into a Nullable&lt;T&gt;, so "BirthDate.Year" on a DateTime? is built as
    /// "BirthDate.Value.Year". Each nullable link is recorded as it is passed, and
    /// <see cref="NotNullCheck"/> turns those into the guard every operator is built on, so the unwrap is never
    /// reached for a null and the member behaves as a nullable in its own right.
    /// </para>
    /// <para>
    /// A reference on the way in is recorded the same way, since it can be missing too: "Lair.Name" against a
    /// minion with no lair used to read through the null and throw. A database answers that through the join, so
    /// guarding it is what makes the two agree, and it costs nothing there the provider folds the check into the
    /// join it was making anyway.
    /// </para></summary>
    /// <param name="parameter"></param>
    /// <param name="propertyPath"></param>
    /// <returns></returns>
    private static GetPropertyExpressionRecord GetPropertyExpression(ParameterExpression parameter, string propertyPath)
    {
        WeequeryException.ThrowIfNull(parameter);
        WeequeryException.ThrowIfNullOrEmpty(propertyPath);

        // Build member expression from the provided path
        Expression exp = parameter;
        List<Expression> linkChecks = new();
        foreach (var segment in propertyPath.Split('.'))
        {
            // A Nullable<T> exposes only its own HasValue and Value, so reaching a member of T means stepping
            // through .Value first: "BirthDate.Year" has to be built as "BirthDate.Value.Year". An explicitly
            // written .Value or .HasValue is left alone, since those are members of the Nullable itself.
            if (IsNullable(exp.Type) && (!HasMember(exp.Type, segment)))
            {
                linkChecks.Add(Expression.Property(exp, "HasValue")); // guard the unwrap that follows
                exp = Expression.Property(exp, "Value");
            }
            else if ((exp != parameter) && (!exp.Type.IsValueType))
            {
                linkChecks.Add(Expression.NotEqual(exp, Expression.Constant(null, exp.Type)));
            }

            try
            {
                exp = Expression.PropertyOrField(exp, segment);
            }
            catch (ArgumentException ex)
            {
                throw new WeequeryException($"Could not resolve '{segment}' of property path '{propertyPath}' on {exp.Type.Name}", ex);
            }
        }
        var lamb = Expression.Lambda(exp, parameter);

        if (!(lamb.Body is MemberExpression memberExpression)) { throw new WeequeryException($"Could not extract MemberExpression for property '{propertyPath}'"); }

        // Since we built this expression from a path string, we should never encounter this, but retaining it for background since the entire Expression area is a very dark corner
        // The body of the lambda could be a MemberExpression directly (e.g., x => x.Property) or a UnaryExpression if it involves a cast (e.g., x => (object)x.Property)
        // if ((lamb.Body is UnaryExpression unaryExpression) && (unaryExpression.Operand is MemberExpression operandMemberExpression))
        // { member = operandMemberExpression; }

        var memberType = GetMemberType(memberExpression);

        return new(memberExpression, memberType, linkChecks);
    }

    /// <summary>
    /// The path a selector points at, in the dotted form the rest of this class works in, so that
    /// <c>(x) =&gt; x.Lair.Capacity</c> gives "Lair.Capacity".
    /// <para>
    /// Read off the member chain rather than out of the lambda's text. 
    /// The text is close enough to be tempting asthe path is in there but it is a debugging aid with no contract behind it, 
    /// and it carries whatever else the compiler put in the tree: a selector whose property type is not TProperty exactly is wrapped in a
    /// conversion, so <c>(x) =&gt; x.Pay</c> and <c>(x) =&gt; (object)x.Pay</c> print differently while meaning the
    /// same path. Stepping over the wrappers is easier than recognising them in a string.
    /// </para>
    /// </summary>
    /// <typeparam name="TProperty"></typeparam>
    /// <param name="selector"></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">the selector is not a chain of members reaching its own parameter</exception>
    private static string GetPropertyPath<TProperty>(Expression<Func<TClass, TProperty>> selector)
    {
        List<string> segments = new();

        var node = Unwrap(selector.Body);
        while (node is MemberExpression member)
        {
            segments.Add(member.Member.Name);
            node = Unwrap(member.Expression);
        }

        // The chain has to arrive at the selector's own parameter. Anything else reaches a value from somewhere
        // else entirely, a captured variable or a static, which is not a property of TClass and cannot be bound.
        if ((segments.Count == 0) || (node != selector.Parameters[0]))
        {
            throw new WeequeryException($"Could not extract a property path from selector '{selector}', it must select a property of {typeof(TClass).Name}, as (x) => x.Name or (x) => x.Lair.Capacity");
        }

        // Collected innermost first, on the way back up to the parameter
        segments.Reverse();

        return string.Join(".", segments);
    }

    /// <summary>
    /// Step over the conversions the compiler inserts where a property's type is not the selector's type exactly,
    /// as boxing an int to select it as an object does
    /// </summary>
    /// <param name="expression"></param>
    /// <returns></returns>
    private static Expression? Unwrap(Expression? expression)
    {
        while ((expression is UnaryExpression unary) && (expression.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.TypeAs))
        {
            expression = unary.Operand;
        }

        return expression;
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
    /// <exception cref="WeequeryException"></exception>
    public static Binding<TClass> Create<TProperty>(ParameterExpression? parameter, Expression<Func<TClass, TProperty>> selector, Dictionary<string, Binding<TClass>>? bindings, string? key = null)
    {
        WeequeryException.ThrowIfNull(selector);
        WeequeryException.ThrowIfNotNullButEmpty(key);
        WeequeryException.ThrowIfNotBindingKey(key);

        var binding = FromPath(parameter, GetPropertyPath(selector));

        return AddTo(bindings, binding, key ?? binding.PropertyPath);
    }

    /// <summary>
    /// Create binding for the property the selector reaches, then the segments after it, so a selector can name a
    /// path it cannot write.
    /// <para>
    /// C# will not let a selector step through a Nullable&lt;&gt;: "(x) =&gt; x.BirthDate.Year" does not compile
    /// against a DateTime?, because Nullable&lt;&gt; exposes only its own members, and naming Value to get past it
    /// unwraps rather than reaches through, giving a plain int with no null of its own. Selecting BirthDate and
    /// naming "Year" as a segment binds "BirthDate.Year" the way the string path does, keeping the compiler's
    /// check on the part it can check. See <see cref="GetPropertyExpression"/> for what reaching through means.
    /// </para>
    /// </summary>
    /// <typeparam name="TProperty"></typeparam>
    /// <param name="parameter">[OPT] all bindings for the same query should share a common parameter</param>
    /// <param name="selector">lambda reaching as far as the compiler can follow (eg. (x)=&gt;x.BirthDate)</param>
    /// <param name="segments">the rest of the path, in order (eg. ["Year"])</param>
    /// <param name="bindings">[OPT] binding LUT to add to, made by <see cref="BindingLookup.Create"/> so keys are matched the same way everywhere</param>
    /// <param name="key">[OPT] key to use to add to LUT, if not provided, the last segment will be used</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException"></exception>
    public static Binding<TClass> Create<TProperty>(ParameterExpression? parameter, Expression<Func<TClass, TProperty>> selector, string[] segments, Dictionary<string, Binding<TClass>>? bindings, string? key = null)
    {
        WeequeryException.ThrowIfNull(selector);
        WeequeryException.ThrowIfNull(segments);
        if (segments.Length == 0) { throw new WeequeryException($"{nameof(segments)} must contain at least one element"); }
        WeequeryException.ThrowIfNotNullButEmpty(key);
        WeequeryException.ThrowIfNotBindingKey(key);

        foreach (var segment in segments) { WeequeryException.ThrowIfNullOrEmpty(segment); }

        var binding = FromPath(parameter, string.Join(".", [GetPropertyPath(selector), .. segments]));

        // The last segment, matching what the segments constructor of a BindingRequest does, since the whole path
        // has periods in it and so cannot be a key
        return AddTo(bindings, binding, key ?? segments[^1]);
    }

    /// <summary>
    /// Put a binding in the lookup under the key it will be asked for by, if there is a lookup to put it in.
    /// </summary>
    /// <param name="bindings">[OPT] where to add it</param>
    /// <param name="binding"></param>
    /// <param name="useKey">the key given, or the one derived for it</param>
    /// <returns>the binding, added or not</returns>
    /// <exception cref="WeequeryException">the key is not a valid name, or is already taken</exception>
    private static Binding<TClass> AddTo(Dictionary<string, Binding<TClass>>? bindings, Binding<TClass> binding, string useKey)
    {
        if (bindings is not null)
        {
            // Covers the derived key as well as an explicit one. Named "key" rather than by the variable it
            // arrived in, since that is what the caller passed or left out.
            WeequeryException.ThrowIfNotBindingKey(useKey, "key");
            // Keys are matched without regard to case, so two that differ only in case are the same key
            if (bindings.ContainsKey(useKey)) { throw new WeequeryException($"Binding already exists for '{useKey}'"); } // could check if values differ, but that seems failure prone
            bindings[useKey] = binding;
        }

        return binding;
    }

    /// <summary>
    /// Create binding for the requested property (as specified by path), optionally adding it to the bindings LUT (optionally with the given key)
    /// </summary>
    /// <param name="parameter">[OPT] all bindings for the same query should share a common parameter</param>
    /// <param name="propertyPath">Path to property, for non-nested properties, this will simply be the property name, for nested it will formatted as X.Y.Z</param>
    /// <param name="bindings">[OPT] binding LUT to add to, made by <see cref="BindingLookup.Create"/> so keys are matched the same way everywhere</param>
    /// <param name="key">[OPT] key to use to add to LUT, if not provided, .PropertyPath will be used</param>
    /// <returns></returns>
    public static Binding<TClass> Create(ParameterExpression? parameter, string propertyPath, Dictionary<string, Binding<TClass>>? bindings, string? key = null)
    {
        WeequeryException.ThrowIfNullOrEmpty(propertyPath);
        WeequeryException.ThrowIfNotNullButEmpty(key);
        WeequeryException.ThrowIfNotBindingKey(key);

        var binding = FromPath(parameter, propertyPath);

        return AddTo(bindings, binding, key ?? binding.PropertyPath);
    }
}