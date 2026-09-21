using System.Reflection;
using Weequery.Builders;

namespace Weequery;

/// <summary>
/// Walks a type and works out what could be bound on it, which is what
/// </summary>
internal static class BindingResolver
{
    /// <summary>
    /// If a property's type is one the caller asked to leave out, see
    /// <see cref="BindingResolutionSettings.IgnoreTypes"/>.
    /// </summary>
    /// <param name="type">the property's declared type</param>
    /// <param name="settings"></param>
    /// <returns></returns>
    internal static bool ShouldIgnoreType(Type type, BindingResolutionSettings settings)
    {
        if (settings.IgnoreTypes.Count == 0) { return false; }
        if (settings.IgnoreTypes.Contains(type)) { return true; }
        if (!settings.IgnoreTypeWhenAssignable) { return false; }
        return settings.IgnoreTypes.Where(ignore => type.IsAssignableTo(ignore)).Any();
    }

    /// <summary>
    /// If a type holds anything worth walking into
    /// </summary>
    /// <param name="type"></param>
    /// <returns></returns>
    internal static bool IsContainer(Type type)
    {
        return type.IsAssignableTo(typeof(System.Collections.IEnumerable));
    }

    /// <summary>
    /// If the walk should descend into a property
    /// </summary>
    /// <param name="type">the property's declared type</param>
    /// <param name="settings"></param>
    /// <param name="path">the property's whole path, which is what the ignore rules are matched against</param>
    /// <param name="ancestors">
    /// the types already gathered on the way here, see <see cref="Inquiry{T}.ResolveBindables(int, BindingResolutionSettings, BindingUse)"/>
    /// </param>
    /// <returns></returns>
    internal static bool ShouldExpandType(Type type, BindingResolutionSettings settings, string path, HashSet<Type> ancestors)
    {
        if (IsContainer(type)) { return false; }
        if (!(type.IsClass || type.IsInterface)) { return false; } // can't possess anything underneath
        if (settings.IgnorePaths.Contains($"{path}.")) { return false; } // we've been asked to ignore underneath this one
        if (settings.DoNotExpandTypes.Contains(type)) { return false; } // we've been asked not to expand this type
        if (ancestors.Contains(type)) { return false; } // this is a loop

        return true;
    }

    /// <summary>
    /// The properties of a type that resolution will consider, prior to any settings
    /// </summary>
    /// <remarks>
    /// <para>
    /// Names are distinct, so a shadowed path will only appear as the first found
    /// </para>
    /// </remarks>
    /// <param name="type"></param>
    /// <returns></returns>
    internal static IEnumerable<PropertyInfo> ReadableProperties(Type type)
    {
        IEnumerable<PropertyInfo> properties = type.GetProperties();

        if (type.IsInterface)
        {
            properties = properties.Concat(type.GetInterfaces().SelectMany(inherited => inherited.GetProperties()));
        }

        return properties
            .Where(prop => prop.CanRead && (prop.GetIndexParameters().Length == 0))
            .GroupBy(prop => prop.Name)
            .Select(group => group.First());
    }

    /// <summary>
    /// The key a resolved path is bound under, which is the path itself unless it is a reserved word
    /// </summary>
    /// <remarks>
    /// If the key would overlap with a reserved query word, it will have a _ appended
    /// </remarks>
    /// <param name="path">the resolved property path</param>
    /// <returns>the path, or the path with an underscore where the path is a reserved word</returns>
    internal static string KeyFor(string path)
    {
        return QueryKeywords.IsReserved(path) ? $"{path}_" : path;
    }

    /// <summary>
    /// Walk one level of a type, adding a request for every readable property that survives the settings, and
    /// recursing into those as appropriate
    /// </summary>
    /// <remarks>
    /// Properties are returned in path order.
    /// </remarks>
    /// <param name="bindings">the list being built, added to in place</param>
    /// <param name="type">the type to walk</param>
    /// <param name="depth">levels already descended, so 0 for the entity itself</param>
    /// <param name="maxDepth">how far down to go, already bounded by the caller</param>
    /// <param name="prefix">the path so far, empty at the top, which is what makes the keys dotted</param>
    /// <param name="settings"></param>
    /// <param name="ancestors">the types found on the path to here, used for loop-checking
    /// </param>
    /// <returns>the same list, for the caller that started it</returns>
    internal static IReadOnlyList<BindingRequest> ResolveBindables(List<BindingRequest> bindings, Type type, int depth, int maxDepth, string prefix, BindingResolutionSettings settings, HashSet<Type> ancestors)
    {
        ancestors.Add(type); // Opened on the way in and closed on the way out

        try
        {
            var properties = ReadableProperties(type).OrderBy(prop => prop.Name);
            foreach (var property in properties)
            {
                var pathName = (string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}.{property.Name}");

                // A value type with no builder cannot be bound
                if (!ExpressionBuilder.CanBindPropertyType(property.PropertyType)) { continue; }

                if ((!settings.IgnorePaths.Contains(pathName)) && (!ShouldIgnoreType(property.PropertyType, settings)))
                {
                    bindings.Add(new(pathName, KeyFor(pathName)));

                    // if we haven't bottomed out, and settings say the property should be expanded
                    if ((depth < maxDepth) && (ShouldExpandType(property.PropertyType, settings, pathName, ancestors)))
                    {
                        ResolveBindables(bindings, property.PropertyType, depth + 1, maxDepth, pathName, settings, ancestors);
                    }
                }
            }
        }
        finally
        {
            ancestors.Remove(type);
        }

        return bindings;
    }
}
