using Weequery.Bindings;
using Weequery.Parsing;
namespace Weequery;

/// <summary>
/// Which bound fields a query reads back, for the caller only wants a subset of the found row.
/// </summary>
/// <remarks>
/// <para>
/// The allow-list is the same one: anything bound is projectable, matched without regard to case, and a field no
/// binding claimed is refused exactly as it is in a condition. So a projection grants nothing that filtering did
/// not already, and there is nothing extra to configure.
/// </para>
/// <code>
/// var rows = query.WithWeequery()
///     .BindProperties(MinionBindings)
///     .ApplyCondition("IsActive = true")
///     .ApplyProjection("Name, Pay")
///     .BuildProjected();          // IQueryable&lt;Dictionary&lt;string, object?&gt;&gt;
/// </code>
/// <para>
/// A field may carry an index, as it may anywhere else: <c>Tallies[apples]</c> projects that one element. It may not be 
/// a bound collection, which has no single value to read, see <see cref="Inquiry{T}.BindCollection"/>.
/// </para>
/// </remarks>
/// <param name="Fields">
/// the keys to read, in the order they were asked for; never null, and empty for the projection that names
/// nothing, see <see cref="None"/>
/// </param>
public record Projection(IReadOnlyList<string> Fields)
{
    /// <summary>
    /// An empty projection set
    /// <para>
    /// Will not read nothing, but will RETURN nothing, the query must still execute, but will distill to nothing.
    /// </para>
    /// </summary>
    public static readonly Projection None = new([]);

    /// <summary>
    /// The field that stands for every field a caller may read, and the suffix that stands for every one under a
    /// prefix: <c>*</c> and <c>Lair.*</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Expanded when the projection is built rather than when it is parsed, because what it expands to is the
    /// binding set, which a <see cref="Projection"/> knows nothing about. That also means it survives a round
    /// trip through <see cref="ToQuery"/> as what the caller wrote.
    /// </para>
    /// <para>
    /// Whatever it expands to is still only what grants <see cref="BindingUse.Projection"/>. A wildcard is a way
    /// of naming the allow-list, not a way around it.
    /// </para>
    /// </remarks>
    public const string Wildcard = "*";

    /// <summary>If this names any fields at all</summary>
    public bool IsEmpty { get { return Fields.Count == 0; } }

    /// <summary>
    /// Read a projection from a comma separated list of field names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A field is written the way a condition or a sort writes one: bare, quoted, or between brackets, with an
    /// index after it where it is taken at one. So the three read alike and a key needing quotes needs them in
    /// the same place everywhere.
    /// </para>
    /// <code>
    /// Name, Pay
    /// [Name], 'Total Pay', Tallies[apples]
    /// </code>
    /// <para>
    /// Since the return type cannot contain duplicates, a field named more than once is only kept once.
    /// </para>
    /// </remarks>
    /// <param name="fields">null, empty or whitespace gives <see cref="None"/></param>
    /// <param name="style"></param>
    /// <returns>never null</returns>
    /// <exception cref="WeequeryException">the list is malformed</exception>
    public static Projection Parse(string? fields, QueryStyle style = QueryStyle.Native)
    {
        return ProjectionParser.Parse(fields, style);
    }

    /// <summary>
    /// Build a projection from keys already in hand, for the caller assembling one rather than reading it.
    /// </summary>
    /// <param name="fields">null gives <see cref="None"/>; duplicates are kept once, as in <see cref="Parse"/></param>
    /// <returns>never null</returns>
    /// <exception cref="WeequeryException">a field is null or empty</exception>
    public static Projection Of(IEnumerable<string>? fields)
    {
        if (fields is null) { return None; }

        List<string> kept = new();
        HashSet<string> seen = new(BindingLookup.KeyComparer);

        foreach (var field in fields)
        {
            WeequeryException.ThrowIfNullOrEmpty(field, nameof(fields));

            if (seen.Add(field)) { kept.Add(field); }
        }

        return (kept.Count == 0) ? None : new Projection(kept);
    }

    /// <summary>
    /// Write the field list back out, such that <see cref="Parse"/> reads it back.
    /// </summary>
    /// <returns>the empty string where nothing is named</returns>
    public string ToQuery()
    {
        return string.Join(", ", from field in Fields select QueryWriter.Field(field));
    }

    /// <summary>
    /// Renders as the field list, see <see cref="ToQuery"/>
    /// </summary>
    /// <returns></returns>
    public override string ToString()
    {
        return ToQuery();
    }
}
