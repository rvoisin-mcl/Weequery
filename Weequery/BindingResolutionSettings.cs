namespace Weequery;

/// <summary>
/// What <see cref="Inquiry{T}.ResolveBindables(int, BindingResolutionSettings, BindingUse)"/> should leave out when it walks a type.
/// </summary>
/// <remarks>
/// <para>
/// Resolution binds everything it can reach, so these are the subtractions. Nothing here is required, and
/// resolving with no settings takes <see cref="Default"/>, which subtracts only the expansion of a string.
/// That is rarely enough on a type that reaches anything sensitive: see the warning on
/// <see cref="Inquiry{T}.ResolveBindables(int, BindingResolutionSettings, BindingUse)"/>.
/// <para>
/// <see cref="Default"/> is where to start when you want the defaults and one more subtraction, since building
/// the record by hand silently gives up the string rule:
/// <code>
/// var settings = BindingResolutionSettings.Default with { IgnorePaths = ["PasswordHash", "Audit."] };
/// </code>
/// </para>
/// </para>
/// </remarks>
/// <param name="IgnorePaths">
/// Paths not to bind, not keys, so "Lair.Capacity" rather than "Capacity".
/// Can also be used to ignore anything after a path, with a trailing .
/// <para>
/// <b>Inside a collection, name the collection first.</b> An element is walked from its own root, so a path in
/// there is reached by saying which collection it is in, using the same spelling
/// <see cref="Inquiry{T}.ListBindings"/> reports, see <see cref="BoundBinding.ElementMarker"/>:
/// <code>
/// "Assignments[]."             bind the collection, and resolve nothing inside it
/// "Assignments[].Lair"         leave Lair out of this collection's elements
/// "Assignments[].Lair."        bind Lair there, and stop at it
/// "Assignments"                take the name away altogether, collection and property both
/// </code>
/// The first of those leaves the property binding alone, so the key is still there to be null tested and
/// indexed and only the quantifier goes, which is the same distinction a trailing period draws everywhere else.
/// </para>
/// <para>
/// A bare path such as "Lair" is matched against the element's own paths as it always was, and so applies inside
/// every collection that has one rather than a named one. Both spellings subtract, so naming both takes both.
/// </para>
/// </param>
/// <param name="IgnoreTypes">
/// Types not to bind a property of. Compared against the property's declared type, so a
/// <see cref="Nullable{T}"/> is its own type rather than the one it wraps.
/// </param>
/// <param name="IgnoreTypeWhenAssignable">
/// If <paramref name="IgnoreTypes"/> also catches anything assignable to one of them. False matches the
/// declared type exactly.
/// </param>
/// <param name="DoNotExpandTypes">
/// Types to bind but not descend into, which is the difference between leaving a property out and stopping at
/// it. For a class you want reachable and testable for null without its insides going on the wire.
/// <para>
/// An array, a List, a Dictionary and a string are all unconditionally stopped at
/// </para>
/// </param>
public record BindingResolutionSettings(HashSet<string> IgnorePaths, HashSet<Type> IgnoreTypes, bool IgnoreTypeWhenAssignable, HashSet<Type> DoNotExpandTypes)
{
    /// <summary>
    /// Copy constructor, which is also where <see cref="IgnorePaths"/> is normalised.
    /// </summary>
    /// <remarks>
    /// Paths are compared the way binding keys are, without regard to case
    /// </remarks>
    /// <param name="settings">what to copy; must not be null</param>
    public BindingResolutionSettings(BindingResolutionSettings settings)
    {
        IgnorePaths = (settings.IgnorePaths.Comparer != StringComparer.OrdinalIgnoreCase) ? settings.IgnorePaths.ToHashSet(StringComparer.OrdinalIgnoreCase) : settings.IgnorePaths; // ensure this is string insensitive
        IgnoreTypes = settings.IgnoreTypes;
        IgnoreTypeWhenAssignable = settings.IgnoreTypeWhenAssignable;
        DoNotExpandTypes = settings.DoNotExpandTypes;
    }

    /// <summary>
    /// Bind everything available
    /// </summary>
    /// <remarks>
    /// <code>
    /// BindingResolutionSettings.Default with { IgnorePaths = ["PasswordHash", "Audit."] }
    /// </code>
    /// </remarks>
    public static BindingResolutionSettings Default { get; } = new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), [], false, []);
}
