namespace Weequery;

/// <summary>
/// One field a query named that nothing bound, and the part of the query it was taken out of. See
/// <see cref="Inquiry{T}.DroppedFields"/>.
/// </summary>
/// <remarks>
/// <para>
/// Only ever produced where a caller asked for <see cref="Inquiry{T}.IgnoreUnboundFields"/>, since that is the
/// only setting under which anything is dropped rather than refused.
/// </para>
/// <para>
/// The name is the key, with any index stripped off, since what is missing is the binding rather than an element
/// of it: a query naming <c>Tallies[apples]</c> and <c>Tallies[pears]</c> reports <c>Tallies</c>, once. Inside a
/// quantifier it is scoped to the collection's own allow-list rather than the entity's, so a query reading
/// <c>Assignments Any (Loot = 3)</c> reports <c>Loot</c>, a field of an assignment and not of the entity.
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
    /// Reads as what happened, for a log line or a message to whoever wrote the query
    /// </summary>
    /// <returns></returns>
    public override string ToString()
    {
        return $"'{Field}' dropped from the {From.ToString().ToLowerInvariant()}, nothing bound it";
    }
}
