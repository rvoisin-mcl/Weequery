using System.Collections.Concurrent;
using System.Reflection;

namespace Weequery;

/// <summary>
/// The four ordering methods on <see cref="Queryable"/>, cached once for the process, and closed over the types a
/// sort clause needs once per pair of them.
/// </summary>
internal static class SortMethods
{
    private static readonly MethodInfo OrderBy = Resolve(nameof(Queryable.OrderBy));
    private static readonly MethodInfo OrderByDescending = Resolve(nameof(Queryable.OrderByDescending));
    private static readonly MethodInfo ThenBy = Resolve(nameof(Queryable.ThenBy));
    private static readonly MethodInfo ThenByDescending = Resolve(nameof(Queryable.ThenByDescending));

    /// <summary>
    /// What applying one sort clause takes: the method to call, closed over the entity and key types, and the
    /// delegate type its key selector has to be a lambda of.
    /// </summary>
    /// <param name="Method">eg. Queryable.OrderBy&lt;Minion, decimal&gt;</param>
    /// <param name="SelectorType">eg. Func&lt;Minion, decimal&gt;</param>
    internal record Clause(MethodInfo Method, Type SelectorType);

    /// <summary>
    /// Closing a generic method and making a generic type are both cheap once and not free per query, and there
    /// are only as many answers as an application has properties to sort on.
    /// </summary>
    private static readonly ConcurrentDictionary<(MethodInfo Open, Type Entity, Type Key), Clause> Clauses = new();

    /// <summary>
    /// What one sort clause needs, ready to call.
    /// </summary>
    /// <param name="direction"></param>
    /// <param name="hasAlreadyBeenSorted">
    /// true once the query carries a sort, since a second clause has to chain with ThenBy rather than restart with
    /// OrderBy, which would discard the first
    /// </param>
    /// <param name="entityType"></param>
    /// <param name="keyType">the type of what is being sorted on, which is the accessor's own type</param>
    /// <returns></returns>
    internal static Clause For(SortDirection direction, bool hasAlreadyBeenSorted, Type entityType, Type keyType)
    {
        var ascending = (direction == SortDirection.Ascending);
        var open = (hasAlreadyBeenSorted) ? (ascending) ? ThenBy : ThenByDescending : (ascending) ? OrderBy : OrderByDescending;

        return Clauses.GetOrAdd((open, entityType, keyType), static key => new Clause(
            key.Open.MakeGenericMethod(key.Entity, key.Key),
            typeof(Func<,>).MakeGenericType(key.Entity, key.Key)));
    }

    /// <summary>
    /// The two argument overload of each, so the one taking a comparer is not picked up
    /// </summary>
    /// <param name="name"></param>
    /// <returns></returns>
    private static MethodInfo Resolve(string name)
    {
        return typeof(Queryable).GetMethods().First(method => (method.Name == name) && (method.GetParameters().Length == 2));
    }
}
