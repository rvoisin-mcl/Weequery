namespace Weequery;

/// <summary>
/// An unbound field named in a query, and the part of the query it was taken out of. See
/// <see cref="Inquiry{T}.DroppedFields"/>.
/// </summary>
/// <remarks>
/// <para>
/// Only produced where a caller asked for <see cref="Inquiry{T}.IgnoreUnboundFields"/>
/// </para>
/// <para>
/// The name is the key, shorn of any index
/// </para>
/// </remarks>
/// <param name="Field">the key as the query named it</param>
/// <param name="From">
/// which part of the query lost it: <see cref="BindingUse.Condition"/>, <see cref="BindingUse.Sort"/> or
/// <see cref="BindingUse.Projection"/>. Always exactly one of them, never a combination, since a field is
/// dropped from one place at a time and named once per place.
/// </param>
public record DroppedField(string Field, BindingUse From)
{
    /// <summary>
    /// What was done, for logging or message to caller
    /// </summary>
    /// <returns></returns>
    public override string ToString()
    {
        return $"'{Field}' dropped from the {From.ToString().ToLowerInvariant()}, it does not match a binding";
    }
}
