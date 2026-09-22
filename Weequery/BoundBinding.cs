namespace Weequery;

/// <summary>
/// A binding as it stands on an <see cref="Inquiry{T}"/>: the name it answers to, what it reaches, and what a
/// caller may do with it. See <see cref="Inquiry{T}.ListBindings"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>One shape for everything bound.</b> A property of the entity and a property of a collection's element are
/// both an entry here and read the same way, the second qualified by the collection it is reached through:
/// <code>
/// Name                                  an ordinary property
/// AssignmentsAsMember                   a collection, which Any, All and None are written against
/// AssignmentsAsMember[].Leader.Name     a property of one of its elements
/// </code>
/// </para>
/// <para>
/// A report rather than a request. <see cref="BindingRequest"/> carries the first three of these on the way in,
/// and this is deliberately not that: what comes back describes an allow-list that has already been built,
/// including the parts of it no request could have made, so it does not feed back into
/// <see cref="Inquiry{T}.BindProperties"/>.
/// </para>
/// </remarks>
/// <param name="Key">
/// the name a condition, a sort or a projection writes. An element's is qualified, see
/// <see cref="ElementMarker"/>, and is the one key here that is not written as it stands
/// </param>
/// <param name="Path">
/// the property path on the entity, dotted where it is nested and qualified the same way <see cref="Key"/> is. A
/// constant has no property to point at, so its path is the key it was bound under, and a collection's is the
/// collection property itself
/// </param>
/// <param name="Use">
/// what it may be used for, see <see cref="BindingUse"/>. Where one key is both a property and a collection the
/// two grants are added together, since it is one name answering both
/// </param>
public record BoundBinding(string Key, string Path, BindingUse Use)
{
    /// <summary>
    /// What stands in a key where an index would go, marking the step from a collection to one of its elements.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Empty brackets because the bracket is where an element is chosen and this one has not chosen. Substitute an
    /// index into a <see cref="Path"/> carrying it and you have a path that binds,
    /// <c>AssignmentsAsMember[0].Leader.Name</c>, which is how you would reach one element by hand.
    /// </para>
    /// <para>
    /// <b>A qualified key names a binding; it is not a condition.</b> A condition reaches an element by quantifying,
    /// <c>AssignmentsAsMember Any (Leader.Name = 'Max')</c>, which is what <see cref="ElementKey"/> is for, or by
    /// naming one element outright, <c>AssignmentsAsMember[0] IsNotNull</c>. A dotted path after an index is a
    /// binding rather than a field, so asking about one element's far side means binding it under a key of its own.
    /// </para>
    /// <para>
    /// It cannot be mistaken for a real index, an empty one being refused wherever a field is read, and it is
    /// never a key a caller writes: a key holding a bracket is refused at binding time precisely so that
    /// "Labels[0]" cannot be both a key and element zero of Labels.
    /// </para>
    /// </remarks>
    public const string ElementMarker = "[]";

    /// <summary>
    /// Whether a quantifier may be written against <see cref="Key"/>.
    /// </summary>
    /// <remarks>
    /// The one thing about a binding that <see cref="Use"/> cannot tell you. A quantifier is a condition, so a
    /// collection grants <see cref="BindingUse.Test"/> exactly as an ordinary property does, and "Name" and
    /// "AssignmentsAsMember" would otherwise be indistinguishable.
    /// </remarks>
    public bool IsCollection { get; init; }

    /// <summary>
    /// The collection this is an element of, and null for everything reached off the entity directly.
    /// </summary>
    /// <remarks>
    /// Non-null is what makes this entry one that cannot be named as it stands: it needs an index, or it needs
    /// to be inside a quantifier over the key this names.
    /// </remarks>
    public string? ElementOf { get; init; }

    /// <summary>
    /// What to write for this inside a quantifier, and null for everything that is not an element.
    /// </summary>
    /// <remarks>
    /// <see cref="Key"/> qualified by the collection is what identifies the binding across the whole listing;
    /// this is the part of it a condition scoped to an element actually names, the quantifier having already
    /// said which collection:
    /// <code>
    /// AssignmentsAsMember Any (Leader.Name = 'Max')
    /// //                       ^^^^^^^^^^^ ElementKey, where Key is AssignmentsAsMember[].Leader.Name
    /// </code>
    /// </remarks>
    public string? ElementKey { get; init; }

    /// <summary>
    /// What is bound, for logging or for showing a caller what they may ask about
    /// </summary>
    /// <returns></returns>
    public override string ToString()
    {
        var qualified = new List<string>(2);

        // The path is worth printing only where it says something the key did not
        if (!Key.Equals(Path, StringComparison.OrdinalIgnoreCase)) { qualified.Add($"which is {Path}"); }

        if (IsCollection) { qualified.Add("a collection"); }

        if (ElementOf is not null) { qualified.Add($"written '{ElementKey}' inside {ElementOf}"); }

        var about = (qualified.Count == 0) ? "" : $", {string.Join(" and ", qualified)},";

        return $"'{Key}'{about} may be used for {Use.ToString().ToLowerInvariant()}";
    }
}
