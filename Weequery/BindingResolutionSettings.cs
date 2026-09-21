namespace Weequery;

/// <summary>
/// What <see cref="Inquiry{T}.ResolveBindables(int, BindingResolutionSettings)"/> should leave out when it walks a type.
/// </summary>
/// <remarks>
/// <para>
/// Resolution binds everything it can reach, so these are the subtractions. Nothing here is required, and
/// resolving with no settings takes <see cref="Standard"/>, which subtracts only the expansion of a string.
/// That is rarely enough on a type that reaches anything sensitive: see the warning on
/// <see cref="Inquiry{T}.ResolveBindables(int, BindingResolutionSettings)"/>.
/// <para>
/// <see cref="Standard"/> is where to start when you want the defaults and one more subtraction, since building
/// the record by hand silently gives up the string rule:
/// <code>
/// var settings = BindingResolutionSettings.Standard with { IgnorePaths = ["PasswordHash", "Audit."] };
/// </code>
/// </para>
/// </para>
/// </remarks>
/// <param name="IgnorePaths">
/// Paths not to bind, matched without regard to case against the whole path as it would be keyed, so
/// "Lair.Capacity" rather than "Capacity".
/// <para>
/// A trailing period says something different from the path alone, and the difference is worth knowing.
/// "Lair" leaves out the Lair property <b>and</b> everything under it, since a path that is not bound is not
/// descended into either. "Lair." binds Lair itself and stops there, so it can still be tested for null while
/// its properties stay unreachable.
/// </para>
/// </param>
/// <param name="IgnoreTypes">
/// Types not to bind a property of. Compared against the property's declared type, so a
/// <see cref="Nullable{T}"/> is its own type rather than the one it wraps. A property left out this way is
/// not descended into either.
/// </param>
/// <param name="IgnoreTypeWhenAssignable">
/// Whether <paramref name="IgnoreTypes"/> also catches anything assignable to one of them, so an entry for a
/// base class or an interface leaves out everything deriving from or implementing it. False matches the
/// declared type exactly.
/// </param>
/// <param name="DoNotExpandTypes">
/// Types to bind but not descend into, which is the difference between leaving a property out and stopping at
/// it. For a class you want reachable and testable for null without its insides going on the wire.
/// <para>
/// Nothing needs to be here to keep a container out. An array, a List, a Dictionary and a string are all
/// unconditionally stopped at, since what is inside one is not reachable from a filter and what is on the
/// outside is bookkeeping.
/// </para>
/// <para>
/// The path form of the same thing is a trailing period in <paramref name="IgnorePaths"/>, which stops at one
/// property rather than at every property of a type.
/// </para>
/// </param>
public record BindingResolutionSettings(HashSet<string> IgnorePaths, HashSet<Type> IgnoreTypes, bool IgnoreTypeWhenAssignable, HashSet<Type> DoNotExpandTypes)
{
    /// <summary>
    /// Copy constructor, which is also where <see cref="IgnorePaths"/> is normalised.
    /// </summary>
    /// <remarks>
    /// Paths are compared the way binding keys are, without regard to case, so a set built with the default
    /// comparer is rebuilt with the right one. A caller who already supplied a case-insensitive set keeps it
    /// rather than paying for a copy.
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
    /// Subtract nothing, which is what resolving with no settings does.
    /// </summary>
    /// <remarks>
    /// The rules that keep a container, an indexer and an unbindable struct out of a resolved set are not
    /// settings and cannot be turned off, so subtracting nothing still gives a sensible set. What is left here is
    /// the model's own shape, and only you know which parts of that a caller should see.
    /// <para>
    /// Public, and the place to start when you want a subtraction, so that a later default arrives on its own:
    /// </para>
    /// <code>
    /// BindingResolutionSettings.Standard with { IgnorePaths = ["PasswordHash", "Audit."] }
    /// </code>
    /// </remarks>
    public static BindingResolutionSettings Standard { get; } = new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), [], false, []);
}
