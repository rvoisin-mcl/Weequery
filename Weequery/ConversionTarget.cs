namespace Weequery;

/// <summary>
/// Which side of a comparison a <see cref="ValueConverter"/> runs against.
/// </summary>
/// <remarks>
/// <para>
/// A comparison has two sides. One is the bound value, the other is the value supplied for the test. 
/// Converting neither, one, the other, or both are distinct options, chosen when the binding is created
/// meant.
/// <b>Only used for comparisons.</b> Sorting and projecting read the value as stored. See
/// <see cref="ValueConverter"/> for why.
/// </para>
/// </remarks>
[Flags]
public enum ConversionTarget
{
    /// <summary>
    /// Neither side, the converter never runs.
    /// </summary>
    None = 0,

    /// <summary>
    /// The value the caller supplied, converted once as the query is built. Every value the operator takes goes
    /// through it: both ends of a range, every entry of an <see cref="Operator.IsIn"/> list.
    /// </summary>
    Value = 1,

    /// <summary>
    /// The value in the row, converted by the query itself. Against a database this becomes part of the SQL, so
    /// the conversion has to be one the provider can translate: <c>LOWER(column)</c> is fine, a call into your
    /// own code is not.
    /// </summary>
    /// <remarks>
    /// Be aware of the cost. A converted column is an expression rather than a column, it will not be indexed unless 
    /// the database has a matching conversion for that expression. Folding a million rows to compare
    /// against one value is decidently the slow path; normalising the stored data once is the fast one.
    /// </remarks>
    Binding = 2,

    /// <summary>
    /// Both, conversion applies to both sides. The default.
    /// </summary>
    Both = Value | Binding,
}
