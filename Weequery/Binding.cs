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
    private static Expression BuildNotNullCheck(Expression accessor, Type accessorType, bool wrapped, List<Expression> linkChecks)
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
    private Binding(ParameterExpression parameter, string name, Expression accessor, Type accessorType, List<Expression> linkChecks, bool isConstant)
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

        // Settled before the builder check below, since a collection has no builder of its own and would
        // otherwise be flattened to object before anyone could ask what it holds
        Index = isConstant ? null : IndexingFor(PropertyType);

        // If the property type is not something that is supported by a builder type, treat it as an object, which will at least support IsNull
        if (!ExpressionBuilder.HasBuilderForBinding(this))
        {
            if (UnwrappedPropertyType.IsValueType) { throw new WeequeryException($"Could not generate Binding for '{name}', property type {UnwrappedPropertyType.Name} is unsupported"); }

            UnwrappedPropertyType = typeof(object);
        }

        PropertyPath = name;
    }

    /// <summary>
    /// A binding for one element of this collection, which is what a condition naming an index compares against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The element is treated exactly as a <see cref="Nullable{T}"/> is, and for the same reason: it may not be
    /// there. An index past the end of a list, or a key no dictionary holds, is not an error and not a default
    /// value, it is the absence of a value. So the presence test joins the link checks the path already carries,
    /// which makes <see cref="AccessorIsNullable"/> true and hands every operator the same guard a nullable
    /// property gets: a missing element satisfies nothing except IsNull, and the negative operators do not catch
    /// it either.
    /// </para>
    /// <para>
    /// Two checks go on, in order, because the second is only safe once the first holds: the collection is
    /// there, and then it has something at that index. They are ANDed ahead of the read, and both the in-memory
    /// evaluator and a provider short circuit, so the read never happens against a missing collection.
    /// </para>
    /// </remarks>
    /// <param name="index">the index as text, read against the collection's key type</param>
    /// <returns>a binding for the element, nullable whatever the element type is</returns>
    /// <exception cref="WeequeryException">this binding takes no index, or the text is not one</exception>
    public Binding<TClass> Indexed(string index)
    {
        List<Expression> checks = new(LinkChecks);

        var (access, elementType) = IndexInto(Accessor, PropertyType, index, PropertyPath, checks);

        return new Binding<TClass>(Parameter, $"{PropertyPath}[{index}]", access, elementType, checks, isConstant: false);
    }

    /// <summary>
    /// True when the collection holds something at this index. ContainsKey for a dictionary; for a list or an
    /// array, in range and not negative, which is the same question asked of a count.
    /// </summary>
    private static Expression PresenceCheck(Expression container, Type containerType, Indexing indexing, object key)
    {
        var index = Expression.Constant(key, indexing.KeyType);

        if (indexing.IsDictionary)
        {
            var contains = Members(containerType)
                .Select(candidate => candidate.GetMethod("ContainsKey", [indexing.KeyType]))
                .FirstOrDefault(method => method is not null)
                ?? throw new WeequeryException($"(Should be impossible) {containerType.Name} has no ContainsKey");

            return Expression.Call(container, contains, index);
        }

        var count = containerType.IsArray
            ? Expression.ArrayLength(container)
            : (Expression)Expression.Property(container, CountProperty(containerType));

        return Expression.AndAlso(
            Expression.GreaterThanOrEqual(index, Expression.Constant(0)),
            Expression.LessThan(index, count));
    }

    /// <summary>
    /// Read the element. Safe only behind <see cref="PresenceCheck"/>, which is why the two are added together.
    /// </summary>
    private static Expression ElementAccess(Expression container, Type containerType, Indexing indexing, object key)
    {
        var index = Expression.Constant(key, indexing.KeyType);

        if (containerType.IsArray) { return Expression.ArrayIndex(container, index); }

        var indexer = Members(containerType)
            .Select(candidate => candidate.GetProperty("Item", [indexing.KeyType]))
            .FirstOrDefault(property => property is not null)
            ?? throw new WeequeryException($"(Should be impossible) {containerType.Name} has no indexer");

        // Called rather than accessed as an index. The two mean the same thing, but only one of them is what a
        // C# lambda compiles to, and a provider matches the shape it was built to expect: given an
        // IndexExpression, EF Core stops recognising the collection as one it can reach into and rewrites the
        // whole thing into a subquery it then cannot translate. See the remarks on Indexed.
        var getter = indexer.GetGetMethod()
            ?? throw new WeequeryException($"(Should be impossible) {containerType.Name} has an indexer that cannot be read");

        return Expression.Call(container, getter, index);
    }

    /// <summary>
    /// The Count a list is measured by, wherever it is declared
    /// </summary>
    private static PropertyInfo CountProperty(Type containerType)
    {
        return Members(containerType)
            .Select(candidate => candidate.GetProperty("Count"))
            .FirstOrDefault(property => property is not null)
            ?? throw new WeequeryException($"(Should be impossible) {containerType.Name} has no Count");
    }

    /// <summary>
    /// Where to look a member up: the type itself, then its interfaces.
    /// </summary>
    /// <remarks>
    /// The order is not a preference. A provider translates an indexer and a count by recognising the member,
    /// and it recognises <c>List&lt;T&gt;.Item</c> where it does not recognise <c>IList&lt;T&gt;.Item</c>, so
    /// taking the interface first builds a tree that runs in memory and refuses to become SQL. The interfaces are
    /// still searched, for a property whose declared type is one of them.
    /// </remarks>
    /// <param name="type"></param>
    /// <returns></returns>
    private static IEnumerable<Type> Members(Type type)
    {
        return type.GetInterfaces().Prepend(type);
    }

    /// <summary>
    /// The element of a collection at an index, and what has to hold for reading it to be safe.
    /// </summary>
    /// <remarks>
    /// Shared by the two ways an index arrives: named by a condition against a bound collection, see
    /// <see cref="Indexed"/>, and written into a binding path, see <see cref="GetPropertyExpression"/>. Both want
    /// the same two checks in the same order, and both want the result treated as a nullable.
    /// </remarks>
    /// <param name="container">the collection</param>
    /// <param name="containerType">its declared type</param>
    /// <param name="index">the index as text, read against the collection's key type</param>
    /// <param name="name">what to call it if this goes wrong</param>
    /// <param name="checks">the checks to add to, in order: the collection is there, then it holds this</param>
    /// <returns>the element access and its type</returns>
    /// <exception cref="WeequeryException">the type takes no index, or the text is not one</exception>
    private static (Expression Access, Type ElementType) IndexInto(Expression container, Type containerType, string index, string name, List<Expression> checks)
    {
        var indexing = IndexingFor(containerType)
            ?? throw new WeequeryException($"'{name}' cannot be indexed, {containerType.Name} is not a list, an array or a dictionary");

        var key = ValueFormat.Parse(indexing.KeyType, index);

        // The collection itself has to be there before it can be asked anything
        if (!containerType.IsValueType)
        {
            checks.Add(Expression.NotEqual(container, Expression.Constant(null, containerType)));
        }

        checks.Add(PresenceCheck(container, containerType, indexing, key));

        return (ElementAccess(container, containerType, indexing, key), indexing.ElementType);
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
    /// A binding for a application supplied constant value, which reads the same as a property but is the same for
    /// every row.
    /// </summary>
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

        return new Binding<TClass>(useParameter, key, QueryValue.Of(value), typeof(TValue), [], isConstant: true);
    }

    // FIXME Binding<TClass> FromValue<TValue>(ParameterExpression? parameter, string key, TValue value, Func<TValue, TValue>? normalizer) // if provided, normalizer will run against both arguments of a comparison

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

    /// <summary>
    /// What indexing this binding takes, or null where it takes none. See <see cref="Indexed"/>.
    /// </summary>
    /// <param name="KeyType">what the index is read as: int for a list or an array, the key type for a dictionary</param>
    /// <param name="ElementType">what comes back out, which is what the comparison is then against</param>
    /// <param name="IsDictionary">whether presence is asked with ContainsKey rather than against a count</param>
    private record Indexing(Type KeyType, Type ElementType, bool IsDictionary);

    /// <summary>
    /// Whether this binding can be indexed, and how.
    /// </summary>
    public bool IsIndexable { get { return Index is not null; } }

    /// <summary>
    /// The element type an index yields, for a caller that needs to know what it is comparing against
    /// </summary>
    public Type? IndexedElementType { get { return Index?.ElementType; } }

    private Indexing? Index { get; init; }

    /// <summary>
    /// What indexing a type supports: an array or a list by position, a dictionary by key.
    /// </summary>
    /// <remarks>
    /// Asked of the declared type, and of the interfaces it implements rather than of the concrete class, so a
    /// property typed as <see cref="IList{T}"/> or <see cref="IDictionary{TKey, TValue}"/> indexes the same way
    /// the concrete one does. A dictionary is checked for first: one is also a collection of pairs, and indexing
    /// it by position is not what anybody means.
    /// </remarks>
    /// <param name="type"></param>
    /// <returns>null where the type takes no index this understands</returns>
    private static Indexing? IndexingFor(Type type)
    {
        if (type.IsArray && (type.GetArrayRank() == 1))
        {
            return new(typeof(int), type.GetElementType()!, false);
        }

        var interfaces = type.GetInterfaces().Append(type);

        var dictionary = interfaces.FirstOrDefault(candidate => candidate.IsGenericType && (candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>)));
        if (dictionary is not null)
        {
            var arguments = dictionary.GetGenericArguments();

            return new(arguments[0], arguments[1], true);
        }

        var list = interfaces.FirstOrDefault(candidate => candidate.IsGenericType
            && ((candidate.GetGenericTypeDefinition() == typeof(IList<>)) || (candidate.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))));

        return (list is null) ? null : new(typeof(int), list.GetGenericArguments()[0], false);
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
    /// "Lair.Capacity" is two steps and "Assignments[0].LairID" is two as well, the first of them indexed. The
    /// period inside brackets is not a separator, so a dictionary key may hold one: "Tallies[a.b]" is one step
    /// keyed by "a.b" rather than two steps and a broken key.
    /// </para>
    /// <para>
    /// A path is code, not caller input, so what is refused here is a mistake in the calling program rather than
    /// something a stranger typed.
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
            if (name.Length == 0) { throw new WeequeryException($"Property path '{propertyPath}' has a step with no property name"); }

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
                if (close < 0) { throw new WeequeryException($"Property path '{propertyPath}' has a '[' that is never closed"); }

                if (index is not null) { throw new WeequeryException($"Property path '{propertyPath}' indexes one step twice"); }

                index = propertyPath[(i + 1)..close];
                if (index.Length == 0) { throw new WeequeryException($"Property path '{propertyPath}' has an empty index"); }

                i = close;
                continue;
            }

            if (ch == '.') { Finish(); continue; }

            if (index is not null) { throw new WeequeryException($"Property path '{propertyPath}' has text after an index"); }

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

        // One step along the path, which is PropertyOrField except where the type is an interface.
        //
        // An interface does not inherit members the way a class does: reflection reports what the interface
        // itself declares and nothing from the interfaces it is built on, and PropertyOrField asks reflection. So
        // "IPlace.Capacity" resolves and "IPlace.DisplayName", declared on the INamed it extends, does not. The
        // interfaces above are searched only when the interface itself does not declare the name, so one that
        // redeclares a member still wins. Searched rather than left to fail, since a caller naming a member of an
        // interface has no way to know which of its interfaces declared it, and no reason to care.
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
            // A Nullable<T> exposes only its own HasValue and Value, so reaching a member of T means stepping
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
                throw new WeequeryException($"Could not resolve '{step.Name}' of property path '{propertyPath}' on {exp.Type.Name}", ex);
            }

            // An index in the path reads one element and carries on from it. The element behaves as a nullable,
            // exactly as one named by a condition does: the checks go on the same list every other link uses, so
            // a path that indexes past the end is a path with no value rather than one that throws.
            if (step.Index is not null)
            {
                var indexed = IndexInto(exp, exp.Type, step.Index, $"{step.Name}", linkChecks);

                exp = indexed.Access;
                expType = indexed.ElementType;
            }
        }

        // A MemberExpression for an ordinary path, and an index access for one that ends in brackets. Both are
        // read the same way from here; only the type has to be taken from the right place.
        var memberType = (exp is MemberExpression member) ? GetMemberType(member) : expType;

        return new(exp, memberType, linkChecks);
    }

    /// <summary>
    /// An index the compiler wrote into the selector, and what it was taken from.
    /// </summary>
    /// <remarks>
    /// Three shapes reach here, because C# does not settle on one. A list or a dictionary indexes through a call
    /// to the indexer's getter, <c>get_Item</c>; an array is its own <see cref="ExpressionType.ArrayIndex"/> node;
    /// and an <see cref="IndexExpression"/> turns up where a tree was built by hand rather than compiled. All
    /// three mean the element, so all three are read.
    /// <para>
    /// The index has to be a constant. <c>x.Slots[i]</c> over a variable would bind whatever i happened to be
    /// when the binding was made, which reads like it follows the variable and does not, so it is refused.
    /// </para>
    /// </remarks>
    /// <param name="node"></param>
    /// <returns>what was indexed and the index as text, or null where this is not an index</returns>
    private static (Expression Source, string Index)? IndexOf(Expression? node)
    {
        if (node is MethodCallExpression call
            && (call.Object is not null)
            && (call.Arguments.Count == 1)
            && call.Method.Name.Equals("get_Item", StringComparison.Ordinal))
        {
            return (call.Object, ConstantIndex(call.Arguments[0], node));
        }

        if ((node is BinaryExpression binary) && (binary.NodeType == ExpressionType.ArrayIndex))
        {
            return (binary.Left, ConstantIndex(binary.Right, node));
        }

        if (node is IndexExpression indexed && (indexed.Object is not null) && (indexed.Arguments.Count == 1))
        {
            return (indexed.Object, ConstantIndex(indexed.Arguments[0], node));
        }

        return null;
    }

    /// <summary>
    /// The index as the text a path carries, which it can only be if the selector wrote a constant
    /// </summary>
    /// <summary>
    /// A path from the part a selector could reach and the segments named after it.
    /// </summary>
    /// <remarks>
    /// Periods between the segments, except before one that opens with a bracket: an index belongs to the segment
    /// in front of it, so <c>["Slots", "[0]", "Weight"]</c> is "Slots[0].Weight" rather than "Slots.[0].Weight",
    /// which is a step with no property name. Writing the index onto the segment itself, <c>["Slots[0]"]</c>,
    /// works the same way and always did.
    /// </remarks>
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

    private static string ConstantIndex(Expression argument, Expression node)
    {
        if (Unwrap(argument) is not ConstantExpression constant)
        {
            throw new WeequeryException($"'{node}' indexes by something other than a constant, and a binding is made once rather than per row; write the index out, or bind the collection and let the condition name the index");
        }

        return ValueFormat.ToInvariantString(constant.Value);
    }

    /// <summary>
    /// The path a selector points at, in the dotted form the rest of this class works in, so that
    /// <c>(x) =&gt; x.Lair.Capacity</c> gives "Lair.Capacity" and <c>(x) =&gt; x.Slots[0].Weight</c> gives
    /// "Slots[0].Weight".
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

        // Walked from the property back to the parameter, so the segments come out reversed and an index attaches
        // to the segment it was read from, which is the one added next
        string? pendingIndex = null;

        for (; ; )
        {
            if (node is MemberExpression member)
            {
                segments.Add((pendingIndex is null) ? member.Member.Name : $"{member.Member.Name}[{pendingIndex}]");
                pendingIndex = null;
                node = Unwrap(member.Expression);

                continue;
            }

            // An index the compiler wrote: "x.Slots[0]" is a call to the indexer, "x.Scores[0]" on an array is its
            // own node, and both mean the element rather than the collection. Held until the property it belongs
            // to comes round, since the walk arrives at the index first.
            if (IndexOf(node) is (Expression source, string index))
            {
                if (pendingIndex is not null) { throw new WeequeryException($"Could not extract a property path from selector '{selector}': it indexes twice in one step"); }

                pendingIndex = index;
                node = Unwrap(source);

                continue;
            }

            break;
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

        var binding = FromPath(parameter, JoinSegments(GetPropertyPath(selector), segments));

        // The last segment, matching what the segments constructor of a BindingRequest does. The whole path would
        // be a legal key now that a period is one, but this overload has always keyed by the last segment and
        // changing it would rename a key already on the wire. Pass one to get the other.
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
            // Covers the derived key as well as an explicit one, including the one an indexed path would derive,
            // which is refused by name. Called "key" rather than by the variable it arrived in, since that is
            // what the caller passed or left out.
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