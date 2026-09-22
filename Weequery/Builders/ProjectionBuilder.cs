using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Weequery.Bindings;

namespace Weequery.Builders;

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
    /// to be dropped rather than refused, see <see cref="InquirySettings.IgnoreUnboundFields"/>. Null refuses them.
    /// </param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">a field is unbound, names a collection, or does not grant Projection</exception>
    [RequiresDynamicCode(AotMessages.RuntimeGenerics)]
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
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
        var fields = projected.IsEmpty ? Everything(bindings) : Expanded(bindings, projected.Fields, keep);

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
    /// else, see <see cref="InquirySettings.IgnoreUnboundFields"/>. A row with no keys left is a possible answer
    /// here, and the honest one for a caller who asked only for columns that are no longer there.
    /// </remarks>
    /// <summary>
    /// Every key that may be read back, which is what a projection naming nothing means and what
    /// <see cref="Projection.Wildcard"/> expands to.
    /// </summary>
    /// <param name="bindings"></param>
    /// <returns></returns>
    private static List<string> Everything(Dictionary<string, Binding<T>> bindings)
    {
        return [.. from entry in bindings where entry.Value.Allows(BindingUse.Projection) select entry.Key];
    }

    /// <summary>
    /// The fields a caller named, with the wildcards among them replaced by what they stand for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>*</c> stands for every key that may be read, and <c>Lair.*</c> for every one under that prefix. Both
    /// are filtered by <see cref="BindingUse.Projection"/> exactly as a named field is, so neither is a way of
    /// reading something the allow-list did not offer.
    /// </para>
    /// <para>
    /// Expansions and named fields compose: <c>Name, Lair.*</c> is one field and a prefix, and a key arriving
    /// twice is kept once, in the order it was first asked for. Which is why the result is assembled rather than
    /// filtered, where before it was a list the caller already had.
    /// </para>
    /// <para>
    /// A prefix matching nothing is treated as the unbound field it resembles: dropped where the caller asked
    /// for that and refused otherwise. The message names the prefix rather than the whole wildcard, since that
    /// is the part that found nothing.
    /// </para>
    /// </remarks>
    /// <param name="bindings"></param>
    /// <param name="fields">what the caller named, in order</param>
    /// <param name="keep">null to refuse an unbound field, otherwise what decides whether it is dropped</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">a prefix matches nothing and dropping was not asked for</exception>
    private static List<string> Expanded(Dictionary<string, Binding<T>> bindings, IReadOnlyList<string> fields, Func<string, bool>? keep)
    {
        List<string> expanded = [];
        HashSet<string> seen = new(BindingLookup.KeyComparer);

        void Take(IEnumerable<string> keys)
        {
            foreach (var key in keys)
            {
                if (seen.Add(key)) { expanded.Add(key); }
            }
        }

        foreach (var field in fields)
        {
            if (ProjectionWildcard.IsEverything(field))
            {
                Take(Everything(bindings));

                continue;
            }

            if (ProjectionWildcard.Prefix(field) is string prefix)
            {
                var under = Everything(bindings).Where(key => ProjectionWildcard.Under(key, prefix)).ToList();

                if (under.Count == 0)
                {
                    if (keep is not null) { keep(field); continue; }

                    throw new WeequeryException(WeequeryError.UnboundField, $"'{field}' matches nothing: no binding under '{prefix}' can be projected");
                }

                Take(under);

                continue;
            }

            if ((keep is not null) && (!keep(field))) { continue; }

            Take([field]);
        }

        return expanded;
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
            throw new WeequeryException(WeequeryError.OperatorUnsupported, $"'{key}' is a collection, and cannot be projected.");
        }

        return BindingLookup.CanonicalKey(bindings, field);
    }

    /// <summary>
    /// One projected value, boxed, and guarded where the path to it can run through a null
    /// </summary>
    /// <exception cref="WeequeryException">the field is unbound</exception>
    [RequiresDynamicCode(AotMessages.RuntimeGenerics)]
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    private static Expression Value(Dictionary<string, Binding<T>> bindings, string field)
    {
        var binding = BindingLookup.Resolve(bindings, field);

        // Bound, but not for this
        if (!binding.Allows(BindingUse.Projection))
        {
            throw new WeequeryException(WeequeryError.OperatorUnsupported, $"'{field}' cannot be projected: it is only bound for {binding.Use}");
        }

        Expression value = Expression.Convert(binding.Accessor, typeof(object));

        // LinkNotNullCheck guards the read rather than the value
        if (!binding.RequiresLinkCheck) { return value; }

        return Expression.Condition(binding.LinkNotNullCheck, value, Expression.Constant(null, typeof(object)));
    }

}
