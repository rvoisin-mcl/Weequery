using System.Linq.Expressions;
using System.Reflection;
using Weequery.Builders;
using Weequery.Interfaces;

namespace Weequery;

internal class Binding<TClass> : IBinding
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
    /// Whether the property can be put in order, so whether it can be sorted on.
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
    /// Whether the path passes through anything that could be missing, so whether reading the accessor is safe on
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
    public BindingUse Use { get; init; }

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
    /// Normalize a supplied value if appropriate, return it unchanged if not
    /// </summary>
    /// <typeparam name="TValue">the unwrapped property type</typeparam>
    /// <param name="value"></param>
    /// <returns></returns>
    public TValue ConvertClientValue<TValue>(TValue value)
    {
        return ((Converter is null) || (!Converter.Runs(ConversionTarget.Client))) ? value : Converter.Convert(value);
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
                throw new WeequeryException(WeequeryError.ConversionFailed, $"The converter for '{name}' reads a {converter.ValueType.Name}, and the property is a {UnwrappedPropertyType.Name}. A converter is declared for the unwrapped type, so an int? property takes ValueConverter.For<int>");
            }

            Converter = converter;

            if (converter.Runs(ConversionTarget.Source)) { UnwrappedAccessor = converter.Inline(UnwrappedAccessor); }
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
    /// A binding for one element of this collection, which is what a condition naming an index compares against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The element is treated exactly as a <see cref="Nullable{T}"/> is, and out of range index will be treated as NULL
    /// </para>
    /// </remarks>
    /// <param name="index">the index as text, read against the collection's key type</param>
    /// <returns>a binding for the element, nullable whatever the element type is</returns>
    /// <exception cref="WeequeryException">this binding takes no index, or the text is not one</exception>
    public Binding<TClass> Indexed(string index)
    {
        List<Expression> checks = new(LinkChecks);

        var (access, elementType) = IndexInto(Accessor, PropertyType, index, PropertyPath, checks);

        return new Binding<TClass>(Parameter, $"{PropertyPath}[{index}]", access, elementType, checks, isConstant: false, Use, converter: null);
    }

    /// <summary>
    /// Evaluate as True when the collection holds something at this index. ContainsKey for a dictionary; in range for for a list or array.
    /// </summary>
    private static Expression PresenceCheck(Expression container, Type containerType, Indexing indexing, object key)
    {
        var index = Expression.Constant(key, indexing.KeyType);

        if (indexing.IsDictionary)
        {
            var contains = Members(containerType)
                .Select(candidate => candidate.GetMethod("ContainsKey", [indexing.KeyType]))
                .FirstOrDefault(method => method is not null) ?? throw new WeequeryException(WeequeryError.Internal, $"(Should be impossible) {containerType.Name} does not have .ContainsKey()");

            return Expression.Call(container, contains, index);
        }

        var count = containerType.IsArray ? Expression.ArrayLength(container) : (Expression)Expression.Property(container, CountProperty(containerType));

        return Expression.AndAlso(Expression.GreaterThanOrEqual(index, Expression.Constant(0)), Expression.LessThan(index, count));
    }

    /// <summary>
    /// Read the element. Must be used in concert with <see cref="PresenceCheck"/>
    /// </summary>
    private static Expression ElementAccess(Expression container, Type containerType, Indexing indexing, object key)
    {
        var index = Expression.Constant(key, indexing.KeyType);

        if (containerType.IsArray) { return Expression.ArrayIndex(container, index); }

        var indexer = Members(containerType)
            .Select(candidate => candidate.GetProperty("Item", [indexing.KeyType]))
            .FirstOrDefault(property => property is not null) ?? throw new WeequeryException(WeequeryError.Internal, $"(Should be impossible) {containerType.Name} does not have .Item()");

        // Called rather than accessed as an index. EF Core cannot resolve a generated IndexExpression. See the remarks on Indexed.
        var getter = indexer.GetGetMethod() ?? throw new WeequeryException(WeequeryError.Internal, $"(Should be impossible) {containerType.Name} has an inacessible Item()");

        return Expression.Call(container, getter, index);
    }

    /// <summary>
    /// The Count a list is measured by, wherever it is declared
    /// </summary>
    private static PropertyInfo CountProperty(Type containerType)
    {
        return Members(containerType)
            .Select(candidate => candidate.GetProperty("Count"))
            .FirstOrDefault(property => property is not null) ?? throw new WeequeryException(WeequeryError.Internal, $"(Should be impossible) {containerType.Name} does not have .Count");
    }

    /// <summary>
    /// Where to look a member up: the type itself, then its interfaces.
    /// </summary>
    /// <remarks>
    /// The order is not a preference. A provider translates an indexer and a count by recognising the member,
    /// and it recognises <c>List&lt;T&gt;.Item</c> where it does not recognise <c>IList&lt;T&gt;.Item</c>, so
    /// taking the interface first builds a tree that runs in memory and refuses to become SQL. The interfaces are
    /// still searched for a property whose declared type is one of them.
    /// </remarks>
    /// <param name="type"></param>
    /// <returns></returns>
    private static IEnumerable<Type> Members(Type type)
    {
        return type.GetInterfaces().Prepend(type);
    }

    private record IndexIntoRecord(Expression Access, Type ElementType);

    /// <summary>
    /// The element of a collection at an index, and what has to hold for reading it to be safe.
    /// </summary>
    /// <remarks>
    /// An index can be via a Binding against a specific collection index, or via a Condition against a bound collection, see
    /// <see cref="Indexed"/>, and written into a binding path, see <see cref="GetPropertyExpression"/>. Both use
    /// the same two checks in the same order, and both need the result treated as a nullable.
    /// </remarks>
    /// <param name="container">the collection</param>
    /// <param name="containerType">its declared type</param>
    /// <param name="index">the index as text, read against the collection's key type</param>
    /// <param name="name">what to call it if this goes wrong</param>
    /// <param name="checks">the checks to add to, in order: the collection is there, then it holds this</param>
    /// <returns>the element access and its type</returns>
    /// <exception cref="WeequeryException">the type takes no index, or the text is not one</exception>
    private static IndexIntoRecord IndexInto(Expression container, Type containerType, string index, string name, List<Expression> checks)
    {
        var indexing = IndexingFor(containerType) ?? throw new WeequeryException(WeequeryError.PathInvalid, $"'{name}' cannot be indexed, {containerType.Name} is not a supported collection");

        var key = ValueFormat.Parse(indexing.KeyType, index);

        // The collection has to exist before it can return anything
        if (!containerType.IsValueType)
        {
            checks.Add(Expression.NotEqual(container, Expression.Constant(null, containerType)));
        }

        checks.Add(PresenceCheck(container, containerType, indexing, key));

        return new(ElementAccess(container, containerType, indexing, key), indexing.ElementType);
    }

    /// <summary>
    /// A binding for the property a path names
    /// </summary>
    /// <param name="parameter">[OPT] all bindings for the same query should share a common parameter</param>
    /// <param name="propertyPath"></param>
    /// <param name="use">what the binding may be used for, see <see cref="BindingUse"/></param>
    /// <param name="converter">[OPT] the normalisation applied to its values, see <see cref="ValueConverter"/></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException"></exception>
    private static Binding<TClass> FromPath(ParameterExpression? parameter, string propertyPath, BindingUse use, ValueConverter? converter)
    {
        WeequeryException.ThrowIfNullOrEmpty(propertyPath);

        var useParameter = parameter ?? Expression.Parameter(typeof(TClass));
        var resolved = GetPropertyExpression(useParameter, propertyPath);

        return new Binding<TClass>(useParameter, propertyPath, resolved.Expression, resolved.ExpressionType, resolved.LinkChecks, isConstant: false, use, converter);
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
    public static Binding<TClass> CreateConstant<TValue>(ParameterExpression? parameter, string key, TValue value, Dictionary<string, Binding<TClass>>? bindings, BindingUse use = BindingUse.All, ValueConverter? converter = null)
    {
        WeequeryException.ThrowIfNullOrEmpty(key);
        WeequeryException.ThrowIfNotBindingKey(key);

        return AddTo(bindings, FromValue(parameter, key, value, use, converter), key);
    }

    /// <summary>
    /// If values of the type can be ordered
    /// </summary>
    /// <param name="type"></param>
    /// <returns></returns>
    private static bool CanBeOrdered(Type type)
    {
        if (typeof(IComparable).IsAssignableFrom(type)) { return true; }

        return type.GetInterfaces().Any(candidate => candidate.IsGenericType && (candidate.GetGenericTypeDefinition() == typeof(IComparable<>)));
    }

    /// <summary>
    /// Record how a binding is indexable. See <see cref="Indexed"/>.
    /// </summary>
    /// <param name="KeyType">the type of the index parameter: int for a list or an array, the key type for a dictionary</param>
    /// <param name="ElementType">what comes back out, which is what the comparison is then against</param>
    /// <param name="IsDictionary">whether presence is asked with ContainsKey rather than against a count</param>
    private record Indexing(Type KeyType, Type ElementType, bool IsDictionary);

    private Indexing? Index { get; init; }

    /// <summary>
    /// Whether this binding can be indexed, and how.
    /// </summary>
    public bool IsIndexable { get { return Index is not null; } }

    /// <summary>
    /// The element type an index yields, for a caller that needs to know what it is comparing against
    /// </summary>
    public Type? IndexedElementType { get { return Index?.ElementType; } }

    /// <summary>
    /// Determine what indexing a type supports, if any
    /// </summary>
    /// <param name="type"></param>
    /// <returns>null if the type is unindexable, or not in a supported fashion</returns>
    private static Indexing? IndexingFor(Type type)
    {
        if (type.IsArray && (type.GetArrayRank() == 1))
        {
            return new(typeof(int), type.GetElementType()!, false);
        }

        var interfaces = type.GetInterfaces().Append(type);

        var dict = interfaces.FirstOrDefault(candidate => candidate.IsGenericType && (candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>)));
        if (dict is not null)
        {
            var arguments = dict.GetGenericArguments();

            return new(arguments[0], arguments[1], true);
        }

        var list = interfaces.FirstOrDefault(candidate => candidate.IsGenericType
            && ((candidate.GetGenericTypeDefinition() == typeof(IList<>)) || (candidate.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))));
        if (list is not null)
        {
            return new(typeof(int), list.GetGenericArguments()[0], false);
        }

        return null;
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
                throw new WeequeryException(WeequeryError.BindingInvalid, $"Could not generate member for expression {expression}");
        }
    }

    private record GetPropertyExpressionRecord(Expression Expression, Type ExpressionType, List<Expression> LinkChecks);

    /// <summary>
    /// One step of a binding path: a property name, and the index to read from it if the path named one.
    /// </summary>
    /// <param name="Name">the property</param>
    /// <param name="Index">what to read out of it, or null to take the property itself</param>
    private record PathStep(string Name, string? Index);

    /// <summary>
    /// Split a binding path into its steps, respecting brackets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Lair.Capacity" is two steps and "Assignments[0].LairID" also two, the first of them indexed. A . inside
    /// a dictionary index will be understood part of the key and not a path seperator
    /// </para>
    /// </remarks>
    /// <param name="propertyPath"></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">the brackets do not close, or a step has no name</exception>
    private static List<PathStep> PathSteps(string propertyPath)
    {
        List<PathStep> steps = new();

        var name = new System.Text.StringBuilder();
        string? index = null;

        void Finish()
        {
            if (name.Length == 0) { throw new WeequeryException(WeequeryError.PathInvalid, $"Property path '{propertyPath}' has a empty stop"); } // // Binding..Child

            steps.Add(new(name.ToString(), index));
            name.Clear();
            index = null;
        }

        for (var i = 0; i < propertyPath.Length; i++)
        {
            var ch = propertyPath[i];

            if (ch == '[')
            {
                var close = propertyPath.IndexOf(']', i);
                if (close < 0) { throw new WeequeryException(WeequeryError.PathInvalid, $"Property path '{propertyPath}' has an unclosed '['"); } // Binding[

                if (index is not null) { throw new WeequeryException(WeequeryError.PathInvalid, $"Property path '{propertyPath}' contains multiple indexes in the same stop"); } // Binding[x][y]

                index = propertyPath[(i + 1)..close];
                if (index.Length == 0) { throw new WeequeryException(WeequeryError.PathInvalid, $"Property path '{propertyPath}' has an empty index"); } // Binding[]

                i = close;
                continue;
            }

            if (ch == '.') 
            { 
                Finish(); 
                continue; 
            }

            if (index is not null) { throw new WeequeryException(WeequeryError.PathInvalid, $"Property path '{propertyPath}' has text after an index"); } // Binding[x]BlahBlah

            name.Append(ch);
        }

        Finish();

        return steps;
    }

    private static bool IsNullable(Type type)
    {
        return type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Nullable<>));
    }

    /// <summary>
    /// If this type contains a property by this name. Matches how <see cref="Expression.PropertyOrField"/>
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
    /// </summary>
    /// <param name="parameter"></param>
    /// <param name="propertyPath"></param>
    /// <returns></returns>
    private static GetPropertyExpressionRecord GetPropertyExpression(ParameterExpression parameter, string propertyPath)
    {
        WeequeryException.ThrowIfNull(parameter);
        WeequeryException.ThrowIfNullOrEmpty(propertyPath);

        // One step along the path, which is PropertyOrField except when the type is an interface.
        static Expression StepInto(Expression on, string segment)
        {
            if (on.Type.IsInterface && (on.Type.GetProperty(segment) is null))
            {
                foreach (var declaring in on.Type.GetInterfaces())
                {
                    var inherited = declaring.GetProperty(segment);

                    if (inherited is not null) { return Expression.Property(on, inherited); }
                }
            }

            return Expression.PropertyOrField(on, segment);
        }

        // Build member expression from the provided path
        Expression exp = parameter;
        Type expType = typeof(TClass);
        List<Expression> linkChecks = new();

        foreach (var step in PathSteps(propertyPath))
        {
            // A Nullable<T> exposes only its own HasValue and Value, so getting a member of T means going
            // through .Value first: "BirthDate.Year" has to be built as "BirthDate.Value.Year". An explicitly
            // written .Value or .HasValue is left alone, since those are members of the Nullable itself.
            if (IsNullable(exp.Type) && (!HasMember(exp.Type, step.Name)))
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
                exp = StepInto(exp, step.Name);
                expType = exp.Type;
            }
            catch (ArgumentException ex)
            {
                throw new WeequeryException(WeequeryError.PathInvalid, $"Could not resolve '{step.Name}' of property path '{propertyPath}' on {exp.Type.Name}", ex);
            }

            // An index in the path reads one element and carries on from it. We will treat the element as a nullable,
            // exactly as one named by a condition does, so a path that indexes outside the collection past the end is a path
            // is a null, and not an exception
            if (step.Index is not null)
            {
                var indexed = IndexInto(exp, exp.Type, step.Index, $"{step.Name}", linkChecks);

                exp = indexed.Access;
                expType = indexed.ElementType;
            }
        }

        // MemberExpression for an a plain old ordinary path
        var memberType = (exp is MemberExpression member) ? GetMemberType(member) : expType;

        return new(exp, memberType, linkChecks);
    }

    private record IndexOfRecord(Expression Source, string Index);

    /// <summary>
    /// An index the compiler wrote into the selector, and what it was taken from.
    /// </summary>
    /// <remarks>
    /// Ther are 3 paths that will lead here, A list or a dictionary indexes through a call
    /// to the indexer's getter, <c>get_Item</c>; an array is its own <see cref="ExpressionType.ArrayIndex"/> node;
    /// and an <see cref="IndexExpression"/> turns up where a tree was built by hand rather than compiled.
    /// <para>
    /// The index must be a constant value
    /// </para>
    /// </remarks>
    /// <param name="node"></param>
    /// <returns>what was indexed and the index as text, or null if there is no index</returns>
    private static IndexOfRecord? IndexOf(Expression? node)
    {
        if (node is MethodCallExpression call
            && (call.Object is not null)
            && (call.Arguments.Count == 1)
            && call.Method.Name.Equals("get_Item", StringComparison.Ordinal))
        {
            return new(call.Object, ConstantIndex(call.Arguments[0], node));
        }

        if ((node is BinaryExpression binary) && (binary.NodeType == ExpressionType.ArrayIndex))
        {
            return new(binary.Left, ConstantIndex(binary.Right, node));
        }

        if (node is IndexExpression indexed && (indexed.Object is not null) && (indexed.Arguments.Count == 1))
        {
            return new(indexed.Object, ConstantIndex(indexed.Arguments[0], node));
        }

        return null;
    }

    /// <summary>
    /// A path from the part a selector could reach and the segments named after it. Will join segments with . unless
    /// the segment is an index
    /// </summary>
    /// <param name="head">the path the selector reached</param>
    /// <param name="segments">the rest, in order</param>
    /// <returns></returns>
    private static string JoinSegments(string head, string[] segments)
    {
        var path = new System.Text.StringBuilder(head);

        foreach (var segment in segments)
        {
            if (!segment.StartsWith('[')) { path.Append('.'); }

            path.Append(segment);
        }

        return path.ToString();
    }

    /// <summary>
    /// The index as a string
    /// </summary>
    /// <param name="argument">what the selector indexed by</param>
    /// <param name="node">the indexing expression it came from</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">the index is not a constant</exception>
    private static string ConstantIndex(Expression argument, Expression node)
    {
        if (Unwrap(argument) is not ConstantExpression constant)
        {
            throw new WeequeryException(WeequeryError.PathInvalid, $"'{node}' index is not a constant");
        }

        return ValueFormat.ToInvariantString(constant.Value);
    }

    /// <summary>
    /// Get the path the property represents. <c>(x) =&gt; x.Lair.Capacity</c> will give gives "Lair.Capacity" and
    /// <c>(x) =&gt; x.Slots[0].Weight</c> gives "Slots[0].Weight".
    /// <para>
    /// Read off the member chain rather than out of the lambda's text. Exists primarily for debugging purposes. 
    /// A selector whose property type is not TProperty exactly is wrapped in a conversion, so <c>(x) =&gt; x.Pay</c>
    /// and <c>(x) =&gt; (object)x.Pay</c> print differently while meaning the same path. Stepping over the 
    /// wrappers is easier than recognising them in a string.
    /// </para>
    /// </summary>
    /// <typeparam name="TProperty"></typeparam>
    /// <param name="selector"></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">the selector is invalid</exception>
    private static string GetPropertyPath<TProperty>(Expression<Func<TClass, TProperty>> selector)
    {
        List<string> segments = new();

        var node = Unwrap(selector.Body);

        // Walk from the property back to the parameter, so the segments come out reversed and an index attaches
        // to the segment it was read from
        string? pendingIndex = null;

        while(true)
        {
            if (node is MemberExpression member)
            {
                segments.Add((pendingIndex is null) ? member.Member.Name : $"{member.Member.Name}[{pendingIndex}]");
                pendingIndex = null;

                node = Unwrap(member.Expression);

                continue;
            }

            // If we found an index, hold on to it until the property it belongs to comes next.
            var idxOf = IndexOf(node);
            if (idxOf is not null)
            {
                if (pendingIndex is not null) { throw new WeequeryException(WeequeryError.PathInvalid, $"Could not extract path from '{selector}': it includes adjacent indexes"); } // No multi-dim [x][y]

                pendingIndex = idxOf.Index;
                
                node = Unwrap(idxOf.Source);

                continue;
            }

            break;
        }

        // The chain must end at the selectors own parameter.
        if ((segments.Count == 0) || (node != selector.Parameters[0]))
        {
            throw new WeequeryException(WeequeryError.PathInvalid, $"Could not extract path from '{selector}', it must select a property of {typeof(TClass).Name}");
        }

        segments.Reverse(); // flip to the expected ordering

        return string.Join(".", segments);
    }

    /// <summary>
    /// Step over any boxing coversions
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
    /// <param name="use">what the binding may be used for, see <see cref="BindingUse"/></param>
    /// <param name="converter">[OPT] the normalisation applied to its values, see <see cref="ValueConverter"/></param>
    /// <exception cref="WeequeryException"></exception>
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
    public static Binding<TClass> Create<TProperty>(ParameterExpression? parameter, Expression<Func<TClass, TProperty>> selector, string[] segments, Dictionary<string, Binding<TClass>>? bindings, string? key = null, BindingUse use = BindingUse.All, ValueConverter? converter = null)
    {
        WeequeryException.ThrowIfNull(selector);
        WeequeryException.ThrowIfNullOrEmpty(segments);
        WeequeryException.ThrowIfNotNullButEmpty(key);
        WeequeryException.ThrowIfNotBindingKey(key);

        // Named for the parameter rather than the loop variable, so a path that names nothing reads the same
        // whether it was empty, null, or a segment that is
        foreach (var segment in segments) { WeequeryException.ThrowIfNullOrEmpty(segment, nameof(segments)); }

        var binding = FromPath(parameter, JoinSegments(GetPropertyPath(selector), segments), use, converter);

        return AddTo(bindings, binding, key ?? binding.PropertyPath);
    }

    /// <summary>
    /// Whether a binding arriving under a key that is already taken is the one already there, so binding it a
    /// second time is a no-op rather than a conflict.
    /// </summary>
    /// <remarks>
    /// One property named by both routes into a set is one binding, and refusing the second call would make the
    /// order they were written in matter. A constant is never the same as anything: it carries a value the path
    /// says nothing about, and its path is its own key, so comparing paths alone would make it a duplicate of
    /// whatever property is bound there and hand that property back in its place, losing the value the caller
    /// supplied without saying so.
    /// </remarks>
    /// <param name="existing">the binding already under the key</param>
    /// <param name="candidate">the one arriving</param>
    /// <returns>true where the two are the same binding</returns>
    internal static bool IsSameBinding(Binding<TClass> existing, Binding<TClass> candidate)
    {
        return !existing.IsConstant && !candidate.IsConstant && (existing.PropertyPath == candidate.PropertyPath);
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
            // Covers the derived key as well as an explicit one, including the one an indexed path would derive
            WeequeryException.ThrowIfNotBindingKey(useKey, "key");

            if (bindings.TryGetValue(useKey, out var existing)) // Keys are case-insensitive
            {
                // The same binding arriving twice is not a conflict, see IsSameBinding for what "the same" means
                if (IsSameBinding(existing, binding))
                {
                    return existing;
                }

                throw new WeequeryException(WeequeryError.KeyTaken, $"Binding already exists for '{useKey}'");
            }

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
    /// <param name="use">what the binding may be used for, see <see cref="BindingUse"/></param>
    /// <param name="converter">[OPT] the normalisation applied to its values, see <see cref="ValueConverter"/></param>
    public static Binding<TClass> Create(ParameterExpression? parameter, string propertyPath, Dictionary<string, Binding<TClass>>? bindings, string? key = null, BindingUse use = BindingUse.All, ValueConverter? converter = null)
    {
        WeequeryException.ThrowIfNullOrEmpty(propertyPath);
        WeequeryException.ThrowIfNotNullButEmpty(key);
        WeequeryException.ThrowIfNotBindingKey(key);

        var binding = FromPath(parameter, propertyPath, use, converter);

        return AddTo(bindings, binding, key ?? binding.PropertyPath);
    }
}