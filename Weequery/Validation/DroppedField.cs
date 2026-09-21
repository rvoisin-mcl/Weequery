namespace Weequery;

/// <summary>
/// An unbound field named in a query, and the part of the query it was taken out of. See
/// <see cref="Inquiry{T}.DroppedFields"/>.
/// </summary>
/// <remarks>
/// <para>
/// Only produced where a caller asked for <see cref="InquirySettings.IgnoreUnboundFields"/>
/// </para>
/// <para>
/// The name is the key, shorn of any index
/// </para>
/// </remarks>
/// <param name="Field">the key as the query named it</param>
/// <param name="From">
/// which part of the query lost it: <see cref="BindingUse.Test"/>, <see cref="BindingUse.Sort"/> or
/// <see cref="BindingUse.Projection"/>. Always exactly one of them, never a combination, since a field is
/// dropped from one place at a time and named once per place.
/// </param>
/// <param name="Reason">
/// Why it went, which is <see cref="Unbound"/> unless something more particular was known. A sort naming a
/// constant or a type with no ordering is dropped as well, and those are bound, so the reason is what keeps the
/// report from claiming otherwise.
/// </param>
public record DroppedField(string Field, BindingUse From, string Reason = DroppedField.Unbound)
{
    /// <summary>
    /// Why most fields are dropped, and what one says when nothing else is given.
    /// </summary>
    /// <remarks>
    /// The default, and most common reason, but not the only possible reason
    /// </remarks>
    public const string Unbound = "it does not match a binding";

    /// <summary>
    /// What was done, for logging or message to caller
    /// </summary>
    /// <returns></returns>
    public override string ToString()
    {
        return $"'{Field}' dropped from the {From.ToString().ToLowerInvariant()}, {Reason}";
    }
}
