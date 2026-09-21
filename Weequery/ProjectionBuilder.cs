using System.Linq.Expressions;

namespace Weequery;

/// <summary>
/// Builds the selector that turns an entity into one projected row, see
/// <see cref="Inquiry{T}.BuildProjected"/>.
/// </summary>
/// <remarks>
/// <para>
/// Its own class because it is the one place a binding becomes something other than a test: everything else in
/// an Inquiry narrows which rows come back, and this decides what a row is. Given what may be read and what was
/// asked for, it hands back a lambda; it holds nothing and decides nothing else.
/// </para>
/// <para>
/// The row is a dictionary built with a collection initializer, which is the shape
/// <c>new Dictionary&lt;string, object?&gt; { ["Name"] = x.Name }</c> compiles to and the shape a provider reads
/// as a list of columns.
/// </para>
/// </remarks>
/// <typeparam name="T">the entity being read</typeparam>
internal static class ProjectionBuilder<T> where T : class
{
    /// <summary>
    /// The selector for a projection.
    /// </summary>
    /// <param name="bindings">what may be read, and under which keys</param>
    /// <param name="collections">the bound collections, which are the one bound thing that cannot be read back</param>
    /// <param name="projected">what was asked for; empty reads everything that grants Projection</param>
    /// <param name="parameter">the shared parameter every accessor hangs off</param>
    /// <param name="keep">
    /// [OPT] if to keep a field that nothing bound, called only where the caller asked for unbound fields
    /// to be dropped rather than refused, see <see cref="Inquiry{T}.IgnoreUnboundFields"/>. Null refuses them.
    /// </param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">a field is unbound, names a collection, or does not grant Projection</exception>
    internal static Expression<Func<T, Dictionary<string, object?>>> Build(
        Dictionary<string, Binding<T>> bindings,
        Dictionary<string, ICollectionBinding<T>> collections,
        Projection projected,
        ParameterExpression parameter,
        Func<string, bool>? keep)
    {
        // A projection that names nothing reads everything a caller may read, and one that names fields reads
        // those. The two stay apart even after dropping: a projection whose every field went asked for some
        // columns and can have none of them, which is not the same as having asked for all of them.
        var fields = projected.IsEmpty
            ? [.. from entry in bindings where entry.Value.Allows(BindingUse.Projection) select entry.Key]
            : Projectable(keep, projected.Fields);

        var add = typeof(Dictionary<string, object?>).GetMethod(nameof(Dictionary<string, object?>.Add))
            ?? throw new WeequeryException(WeequeryError.Internal, $"(Should be impossible) {nameof(Dictionary<string, object?>)} has no Add");

        var entries = from field in fields
                      select Expression.ElementInit(add, Expression.Constant(CanonicalKey(bindings, collections, field)), Value(bindings, field));

        var body = Expression.ListInit(Expression.New(typeof(Dictionary<string, object?>)), entries);

        return Expression.Lambda<Func<T, Dictionary<string, object?>>>(body, parameter);
    }

    /// <summary>
    /// The projected fields, less any the caller asked to have dropped rather than refused.
    /// </summary>
    /// <remarks>
    /// The mildest of the three droppings: a field that goes leaves a key out of the row and changes nothing
    /// else, see <see cref="Inquiry{T}.IgnoreUnboundFields"/>. A row with no keys left is a possible answer
    /// here, and the honest one for a caller who asked only for columns that are no longer there.
    /// </remarks>
    private static IReadOnlyList<string> Projectable(Func<string, bool>? keep, IReadOnlyList<string> fields)
    {
        return (keep is null) ? fields : [.. fields.Where(keep)];
    }

    /// <summary>
    /// The key one projected field reads back under, refusing what cannot be read at all
    /// </summary>
    /// <exception cref="WeequeryException">the field names a bound collection</exception>
    private static string CanonicalKey(Dictionary<string, Binding<T>> bindings, Dictionary<string, ICollectionBinding<T>> collections, string field)
    {
        var key = BindingLookup.SplitIndex(field).Key;

        // Refused here rather than as "unbound", since it is bound and the message would be a lie. A collection
        // holds many values and a column holds one, so there is nothing for this to read.
        if (collections.ContainsKey(key))
        {
            throw new WeequeryException(WeequeryError.OperatorUnsupported, $"'{key}' is a collection, so it cannot be projected: it has no single value to read. Project a field of the entity, or ask about its elements with a quantifier");
        }

        return BindingLookup.CanonicalKey(bindings, field);
    }

    /// <summary>
    /// One projected value, boxed, and guarded where the path to it can run through a null
    /// </summary>
    /// <exception cref="WeequeryException">the field is unbound</exception>
    private static Expression Value(Dictionary<string, Binding<T>> bindings, string field)
    {
        var binding = BindingLookup.Resolve(bindings, field);

        // Bound, but not for this. Said plainly rather than reported as unbound, which would send a caller
        // looking for a typo in a name that works perfectly well in a condition.
        if (!binding.Allows(BindingUse.Projection))
        {
            throw new WeequeryException(WeequeryError.OperatorUnsupported, $"'{field}' cannot be projected: it is bound for {binding.Use}");
        }

        Expression value = Expression.Convert(binding.Accessor, typeof(object));

        // LinkNotNullCheck guards the read rather than the value: an int reached through a null navigation has
        // nothing to box, where an int that is simply zero has
        if (!binding.RequiresLinkCheck) { return value; }

        return Expression.Condition(binding.LinkNotNullCheck, value, Expression.Constant(null, typeof(object)));
    }

}
