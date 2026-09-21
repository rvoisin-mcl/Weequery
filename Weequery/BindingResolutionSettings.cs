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
/// var settings = BindingResolutionSettings.Standard with { IgnorePaths = ["PasswordHash", "Audit."] };
/// </code>
/// </para>
/// </para>
/// </remarks>
/// <param name="IgnorePaths">
/// Paths not to bind, not keys, so "Lair.Capacity" rather than "Capacity".
/// Can also be used to ignore anything after a path, with a trailing .
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
