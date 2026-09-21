namespace Weequery;

/// <summary>
/// Which side of a comparison a <see cref="ValueConverter"/> runs against.
/// </summary>
/// <remarks>
/// <para>
/// A comparison has two sides and they come from different places. One is the <b>client</b> value, the thing the
/// caller wrote in the query; the other is the <b>source</b> value, what the row actually holds. Converting one,
/// the other, or both are three different questions, and only the caller declaring the binding knows which was
/// meant.
/// </para>
/// <code>
/// // The column is stored upper case, so fold what the caller typed and leave the column alone
/// ConversionTarget.Client
///
/// // Neither side is normalised, so fold both and compare like with like
/// ConversionTarget.Both
/// </code>
/// <para>
/// <b>Only comparisons.</b> Sorting and projecting read the value as it is stored, whatever this says, so an
/// order stays the order of the real data and a projected row hands back what is actually in it. See
/// <see cref="ValueConverter"/> for why.
/// </para>
/// </remarks>
[Flags]
public enum ConversionTarget
{
    /// <summary>
    /// Neither side, so the converter never runs. Here because a flags enum needs a zero, and because it is a
    /// way to leave a converter declared and switched off rather than deleting it.
    /// </summary>
    None = 0,

    /// <summary>
    /// The value the caller supplied, converted once as the query is built. Every value the operator takes goes
    /// through it: both ends of a range, every entry of an <see cref="Operator.IsIn"/> list.
    /// </summary>
    Client = 1,

    /// <summary>
    /// The value in the row, converted by the query itself. Against a database this becomes part of the SQL, so
    /// the conversion has to be one the provider can translate: <c>LOWER(column)</c> is fine, a call into your
    /// own code is not.
    /// </summary>
    /// <remarks>
    /// Worth knowing what it costs. A converted column is an expression rather than a column, so an index on it
    /// will not be used unless the database has one on that expression. Folding a million rows to compare
    /// against one value is the slow way round; normalising the stored data once is the fast one.
    /// </remarks>
    Source = 2,

    /// <summary>
    /// Both, which is what makes a comparison agree regardless of how either side was written. The usual answer,
    /// and the default.
    /// </summary>
    Both = Client | Source,
}
