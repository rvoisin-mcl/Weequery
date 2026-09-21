using System.Collections;
using Weequery.Bindings;

namespace Weequery;

/// <summary>
/// A declared allow-list, which keys a caller may use and what they mean. Provided for use by 
/// translating functions.
/// </summary>
/// <remarks>
/// <para>
/// The twin to <see cref="Inquiry{T}"/>'s bindings, for targets that have no entity to walk. What 
/// a key means is stated instead of resolved, but behaves in the same fashion.
/// </para>
/// <para>
/// Keys are case-insensitive, as they are everywhere else in Weequery. What a key carries is the 
/// responsibility of <typeparamref name="TField"/>
/// </para>
/// <code>
/// public sealed class MyFieldSet : FieldSet&lt;MyField&gt;
/// {
///     public MyFieldSet() { }
///     public MyFieldSet(IEnumerable&lt;MyField&gt; fields) : base(fields) { }
/// }
/// </code>
/// </remarks>
/// <typeparam name="TField">what is declared for a key</typeparam>
public abstract class FieldSet<TField> : IEnumerable<TField> where TField : IFieldKey
{
    private readonly Dictionary<string, TField> Fields = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// An empty set, to be added to
    /// </summary>
    protected FieldSet()
    { }

    /// <summary>
    /// A set holding these
    /// </summary>
    /// <param name="fields"></param>
    /// <exception cref="WeequeryException">a field is null, or two claims for one key</exception>
    protected FieldSet(IEnumerable<TField> fields)
    {
        WeequeryException.ThrowIfNull(fields);

        foreach (var field in fields) { Add(field); }
    }

    /// <summary>
    /// Declare a field. Present so a collection initializer works
    /// </summary>
    /// <param name="field"></param>
    /// <returns>this, so it can be chained</returns>
    /// <exception cref="WeequeryException">the field is null, or its key is already taken</exception>
    public FieldSet<TField> Add(TField field)
    {
        WeequeryException.ThrowIfNull(field);
        WeequeryException.ThrowIfNullOrEmpty(field.Key, nameof(field));

        if (Fields.ContainsKey(field.Key)) { throw new WeequeryException(WeequeryError.KeyTaken, $"A field is already declared for '{field.Key}'"); }

        Fields[field.Key] = field;

        return this;
    }

    /// <summary>Find the field matching the key, if it exists</summary>
    /// <param name="key"></param>
    /// <returns></returns>
    public TField? Find(string? key)
    {
        return ((key is not null) && Fields.TryGetValue(key, out var field)) ? field : default;
    }

    /// <summary>
    /// The field for a given key
    /// </summary>
    /// <param name="key"></param>
    /// <param name="what">what is being attempted, for reporting</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">nothing declared the key</exception>
    public TField Resolve(string? key, string what)
    {
        return Find(key) ?? throw new WeequeryException(WeequeryError.UnboundField, $"Unbound field: '{key}' is not declared in the {GetType().Name}, so it cannot be {what}");
    }

    /// <summary>
    /// The declared fields a projection names, with <c>*</c> and <c>Prefix.*</c> expanded to what they stand for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same two wildcards <see cref="Inquiry{T}.ApplyProjection(string?, QueryStyle)"/> takes, meaning the same things,
    /// so a projection written once reads the same whether it is answered from the entity or handed to a
    /// translator. What differs is only what they expand against: here it is the declared set, which has no
    /// <see cref="BindingUse"/> to filter by, so a declared field is a readable one.
    /// </para>
    /// <para>
    /// A key named twice, or named and also caught by a wildcard, is returned once, in the place it was first
    /// asked for. A prefix matching nothing is refused rather than quietly reading nothing, there being no
    /// lenient mode on this side of the wire to drop it into.
    /// </para>
    /// </remarks>
    /// <param name="projection">null or empty gives nothing, which every caller reads as "no projection"</param>
    /// <param name="what">what the fields are wanted for, for the message when one is not declared</param>
    /// <returns>the fields, in the order they were asked for</returns>
    /// <exception cref="WeequeryException">a name is not declared, or a prefix matches nothing declared</exception>
    public IReadOnlyList<TField> Project(Projection? projection, string what)
    {
        if ((projection is null) || projection.IsEmpty) { return []; }

        List<TField> projected = [];
        HashSet<string> seen = new(BindingLookup.KeyComparer);

        void Take(IEnumerable<TField> found)
        {
            foreach (var field in found)
            {
                if (seen.Add(field.Key)) { projected.Add(field); }
            }
        }

        foreach (var name in projection.Fields)
        {
            if (ProjectionWildcard.IsEverything(name))
            {
                Take(this);

                continue;
            }

            if (ProjectionWildcard.Prefix(name) is string prefix)
            {
                var under = this.Where(field => ProjectionWildcard.Under(field.Key, prefix)).ToList();

                if (under.Count == 0)
                {
                    throw new WeequeryException(WeequeryError.UnboundField, $"'{name}' matches nothing: no field under '{prefix}' is declared to {what}");
                }

                Take(under);

                continue;
            }

            Take([Resolve(name, what)]);
        }

        return projected;
    }

    /// <summary>How many fields are declared</summary>
    public int Count { get { return Fields.Count; } }

    /// <summary>The keys, as they were declared</summary>
    public IEnumerable<string> Keys { get { return from declared in Fields.Values select declared.Key; } }

    /// <inheritdoc/>
    public IEnumerator<TField> GetEnumerator()
    {
        return Fields.Values.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
