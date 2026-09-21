namespace Weequery;

/// <summary>
/// Which bound fields a query reads back, for the caller that wants three columns rather than the whole row.
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
/// A field may carry an index, as it may anywhere else: <c>Tallies[apples]</c> projects that one element. What it
/// may not be is a bound collection, which has no single value to read, see
/// <see cref="Inquiry{T}.BindCollection"/>.
/// </para>
/// </remarks>
/// <param name="Fields">
/// the keys to read, in the order they were asked for; never null, and empty for the projection that names
/// nothing, see <see cref="None"/>
/// </param>
public record Projection(IReadOnlyList<string> Fields)
{
    /// <summary>
    /// The projection that names nothing, which is what a null or empty string reads as.
    /// <para>
    /// Not the same as "read nothing": a query with no projection applied reads the whole entity, and
    /// <see cref="Inquiry{T}.BuildProjected"/> with none applied reads every bound field. There is no way to ask
    /// for a row of no columns, and no reason to want one.
    /// </para>
    /// </summary>
    public static readonly Projection None = new([]);

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
    /// A field named twice is kept once, in the position it first appeared. A projection is a set of columns and
    /// a dictionary holds each key once, so there is nothing a duplicate could mean; refusing one would only make
    /// a caller assembling a list from checkboxes deduplicate it first.
    /// </para>
    /// </remarks>
    /// <param name="fields">null, empty or whitespace gives <see cref="None"/></param>
    /// <returns>never null</returns>
    /// <exception cref="WeequeryException">the list is malformed</exception>
    public static Projection Parse(string? fields)
    {
        return ProjectionParser.Parse(fields);
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
