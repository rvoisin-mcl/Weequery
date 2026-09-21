namespace Weequery;

/// <summary>
/// What one query decides for itself, rather than taking from the library. Held on the
/// <see cref="Inquiry{T}"/> it was given to and carried by every copy an Apply or a Bind makes of it.
/// </summary>
/// <remarks>
/// <para>
/// <code>
/// query.WithWeequery(InquirySettings.Default with { StringComparison = StringComparison.InvariantCulture })
/// </code>
/// </para>
/// </remarks>
/// <param name="StringComparison">
/// How the string operators compare, <b>where Weequery is the one comparing</b>: equality, the ordering
/// comparisons, the substring family (<see cref="Operator.StartsWith"/>, <see cref="Operator.EndsWith"/>,
/// <see cref="Operator.Contains"/> and their negatives), the ranges built from the ordering comparisons, and the
/// <see cref="Operator.IsIn"/> family. <see cref="System.StringComparison.Ordinal"/> by default.
/// <para>
/// Ordinal for two reasons. It compares the characters that were stored, which is what a database does, so a
/// condition answers the same either side of that line unless a query asks it not to. And it is the cheaper
/// comparison by a wide margin: a linguistic equality over ten thousand rows costs an order of magnitude more
/// than an ordinal one, see the filtering benchmarks. Ask for a culture where a filter is meant to read the way
/// a person reads, which is the case worth paying for.
/// </para>
/// <para>
/// The null tests are not comparisons and are unaffected, and neither is an equality against a null: what those
/// ask is whether the value is there at all, which no rule has an opinion about.
/// </para>
/// <para>
/// Translated to a database this says nothing: the column collation decides there, and the forms carrying a
/// <see cref="System.StringComparison"/> are not ones a provider translates. So this settles the in-memory half
/// only. Under the default the two halves agree; asking for a culture is what parts them, and a value differing
/// only by an ignorable character is then equal in memory and not equal in the database, see
/// <see cref="Operator"/>.
/// </para>
/// </param>
/// <param name="DefaultPageSize">
/// How many rows a page holds where the caller named no size of its own, see
/// <see cref="Inquiry{T}.ApplyPagination"/>. Null by default, which is no default: a query that asks for no
/// window does not get one, and reads every row the conditions matched.
/// <para>
/// Give it a number and the sense of that inverts, which is the whole point of it. <b>Every</b> query built off
/// these settings is windowed, including the one that never called <see cref="Inquiry{T}.ApplyPagination"/> at
/// all, so a caller cannot ask for a table by omitting a field. A size the caller does name still wins; this is
/// the floor under the ones who name nothing, not a ceiling over the ones who do.
/// </para>
/// <para>
/// Which makes it worth saying out loud that it is not a cap. A caller asking for a page of a million gets a page
/// of a million. Where that matters, clamp <see cref="QueryRequest.PageSize"/> before you hand the request over,
/// somewhere you can say why in the refusal.
/// </para>
/// <para>
/// Must be greater than zero where it is given at all, and refused where it is written rather than read as the
/// null it is not. This is the one end of it that is <b>yours</b>: a default is something you write once, in
/// your own startup, where a zero is a typo and hearing about it immediately is the whole point. The size a
/// <i>caller</i> sends is the other end, is not yours, and is folded rather than refused, see
/// <see cref="Inquiry{T}.ApplyPagination"/>.
/// </para>
/// </param>
public record InquirySettings(StringComparison StringComparison = StringComparison.Ordinal, int? DefaultPageSize = null)
{
    /// <summary>
    /// What a query takes when it is given nothing.
    /// </summary>
    public static InquirySettings Default { get; } = new();

    /// <summary>
    /// Held rather than generated, so that the check below runs on a <c>with</c> as well as on a construction.
    /// The copy constructor a record generates copies fields and then sets only what changed, which means an
    /// initializer on the property would be skipped by every copy and the rule would hold on the first way of
    /// arriving at a bad value and not on the second.
    /// </summary>
    private readonly int? PageSize = Checked(DefaultPageSize);

    /// <summary>
    /// <inheritdoc cref="InquirySettings" path="/param[@name='DefaultPageSize']/node()"/>
    /// </summary>
    /// <exception cref="WeequeryException">it is given and is not greater than zero</exception>
    public int? DefaultPageSize
    {
        get { return PageSize; }

        init { PageSize = Checked(value); }
    }

    /// <summary>
    /// The size, if it is one a page could hold.
    /// </summary>
    /// <remarks>
    /// Refused where it is written rather than read as the null it is not: zero is a page holding nothing, which
    /// is not a thing to arrive at by leaving a field blank.
    /// </remarks>
    /// <param name="size"></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">it is given and is not greater than zero</exception>
    private static int? Checked(int? size)
    {
        if (size is not (null or > 0))
        {
            throw new WeequeryException(WeequeryError.ArgumentInvalid, $"{nameof(DefaultPageSize)} must be > 0 where it is given, {size} is not");
        }

        return size;
    }
}
