namespace Weequery;

/// <summary>
/// What Weequery says when it is asked for something a trimmed or a Native AOT build cannot promise.
/// </summary>
/// <remarks>
/// <para>
/// Three reasons account for all of it, and they are written here once so that a caller who meets two of these
/// warnings does not have to work out whether they are being told the same thing twice.
/// </para>
/// <para>
/// None of these is a defect to be fixed later. Reaching a property by the name a request happens to carry is
/// what the library is for, and a trimmer deciding what to keep before the request exists cannot know the
/// answer. Say which types matter with <see cref="System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembersAttribute"/>,
/// or keep the assemblies holding them, see the DynamicDependency and TrimmerRootDescriptor documentation.
/// </para>
/// </remarks>
internal static class AotMessages
{
    /// <summary>
    /// Reflection over a type a caller named rather than one the compiler saw.
    /// </summary>
    internal const string BoundByName =
        "Weequery reaches properties by the name a caller gives at runtime, so trimming cannot know which ones to keep. "
        + "Annotate the entity types with DynamicallyAccessedMembers, or root them, so the properties a query may name survive.";

    /// <summary>
    /// Generic types and methods closed over a property's type, which is only known once a binding is declared.
    /// </summary>
    internal const string RuntimeGenerics =
        "Weequery closes generic types and methods over the property types it binds, which Native AOT cannot generate at runtime. "
        + "Bindings over reference types are usually fine; a value type that no other code instantiates the same way is not.";

    /// <summary>
    /// System.Text.Json over a condition value whose type is not known until it is read.
    /// </summary>
    internal const string Json =
        "Weequery serializes condition values through System.Text.Json reflection, over a type that is not known until the value is read. "
        + "Use the source generator and give it the closed ConditionValue types this application sends.";
}
