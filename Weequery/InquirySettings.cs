using System.Diagnostics.CodeAnalysis;

namespace Weequery;

/// <summary>
/// Settings that can be configured per query
/// </summary>
/// <remarks>
/// <para>
/// <code>
/// query.WithWeequery(InquirySettings.Default with { StringComparison = StringComparison.InvariantCulture })
/// </code>
/// </para>
/// </remarks>
/// <param name="StringComparison">
/// How the string operators compare, <b>when Weequery is the one comparing</b>: equality, the ordering
/// comparisons, the substring family (<see cref="Operator.StartsWith"/>, <see cref="Operator.EndsWith"/>,
/// <see cref="Operator.Contains"/> and their negatives), the ranges built from the ordering comparisons, and the
/// <see cref="Operator.IsIn"/> family. <see cref="System.StringComparison.Ordinal"/> by default.
/// <para>
/// When the query is handled by EF, this setting will not be applied, and string comparison will be up to the DB
/// settings
/// </para>
/// </param>
/// <param name="DefaultPageSize">
/// How many rows a page holds if no value was provided. Only application if pagnation is requested,  
/// <see cref="Inquiry{T}.ApplyPagination"/>. Must be greater than 0
/// </param>
/// <param name="Operators">
/// Which operators whatever is going to run this query can actually run. Null will be treated as
/// <see cref="OperatorSupport.Everything"/>
/// <para>
/// A condition using one that is not in the set is refused where every other refusal happens: thrown by
/// <see cref="Inquiry{T}.Build"/> and reported by <see cref="Inquiry{T}.Validate()"/>. Set it where the
/// backend is chosen, which is where what it cannot do is known.
/// </para>
/// </param>
/// <param name="IgnoreUnboundFields">
/// If references to unbound fields should be silently dropped instead of refusing the query.
/// <para>
/// <b>Off by default</b> Intended to cover cases where the publically exposed surface varies and stored queries
/// against older versions exist.
/// <code>
/// query.WithWeequery(InquirySettings.Default with { IgnoreUnboundFields = true })
///     .ApplyCondition("IsActive = true AND Gizmo = 3")   // Gizmo is unbound, so this filters on IsActive alone
/// </code>
/// </para>
/// <para>
/// <b>Dropping always widens.</b> Dropped filters will never return less rows than the original, if everything
/// is dropped, the query will return <b>every</b> row.
/// </para>
/// <para>
/// <b>Only genuinely unbound fields go.</b> A field that is bound but not for the requested use,
/// see <see cref="BindingUse"/>, is a deliberate choice. Those are still refused.
/// </para>
/// <para>
/// Any removed fields are reported in <see cref="Inquiry{T}.DroppedFields"/>
/// </para>
/// </param>
public record InquirySettings(StringComparison StringComparison = StringComparison.Ordinal, int? DefaultPageSize = null, OperatorSupport? Operators = null, bool IgnoreUnboundFields = false)
{
    /// <summary>
    /// Default query settings if none were provided
    /// </summary>
    public static InquirySettings Default { get; } = new();

    /// <summary>
    /// Held instead of evaluating on request, so that the check runs on a <c>with</c> as well as a ctor.
    /// </summary>
    private readonly int? PageSize = Checked(DefaultPageSize);

    /// <summary>
    /// Held so the null a caller can pass becomes the everything they meant, on a <c>with</c> as on a ctor.
    /// </summary>
    private readonly OperatorSupport Support = Operators ?? OperatorSupport.Everything;

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
    /// <inheritdoc cref="InquirySettings" path="/param[@name='Operators']/node()"/>
    /// </summary>
    /// <remarks>
    /// Null can be set (evaluated as .Everything), but will never be returned
    /// </remarks>
    [AllowNull]
    public OperatorSupport Operators
    {
        get { return Support; }

        init { Support = value ?? OperatorSupport.Everything; }
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
            throw new WeequeryException(WeequeryError.ArgumentInvalid, $"{nameof(DefaultPageSize)} must be > 0, {size} was received");
        }

        return size;
    }
}
