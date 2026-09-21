using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using Weequery.Parsing;

namespace Weequery.Bindings;

// A binding that can be indexed, and what indexing one means: Tallies[apples] read against a dictionary, a
// list or an array, with the presence check that keeps a key nothing holds from throwing.
internal partial class Binding<TClass>
{
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
    [RequiresDynamicCode(AotMessages.RuntimeGenerics)]
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    public Binding<TClass> Indexed(string index)
    {
        List<Expression> checks = new(LinkChecks);

        var (access, elementType) = IndexInto(Accessor, PropertyType, index, PropertyPath, checks);

        return new Binding<TClass>(Parameter, $"{PropertyPath}[{index}]", access, elementType, checks, isConstant: false, Use, converter: null);
    }

    /// <summary>
    /// Evaluate as True when the collection holds something at this index. ContainsKey for a dictionary; in range for for a list or array.
    /// </summary>
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
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
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
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
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
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
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
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
    [RequiresDynamicCode(AotMessages.RuntimeGenerics)]
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
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
    /// Record how a binding is indexable. See <see cref="Indexed"/>.
    /// </summary>
    /// <param name="KeyType">the type of the index parameter: int for a list or an array, the key type for a dictionary</param>
    /// <param name="ElementType">what comes back out, which is what the comparison is then against</param>
    /// <param name="IsDictionary">if presence is asked with ContainsKey rather than against a count</param>
    private record Indexing(Type KeyType, Type ElementType, bool IsDictionary);

    private Indexing? Index { get; init; }

    /// <summary>
    /// If this binding can be indexed, and how.
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
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
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

}
