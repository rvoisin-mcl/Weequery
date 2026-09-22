using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Weequery.Builders;
using Weequery.Parsing;

namespace Weequery.Bindings;

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
    /// What a collection holds, where that is something a quantifier could ask about.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null for anything that is not a collection of objects, which is the same rule a
    /// <see cref="CollectionBindingSet{TElement}"/> already applies by requiring a class. A string is a sequence
    /// of characters, a dictionary a sequence of pairs, and a list of strings has no property of an element to
    /// name, so none of the three is one. Index those instead.
    /// </para>
    /// </remarks>
    /// <param name="type">the property's declared type</param>
    /// <returns>the element type, or null where there is nothing an element could be asked about</returns>
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    internal static Type? ElementTypeOf(Type type)
    {
        if ((type == typeof(string)) || (!IsContainer(type))) { return null; }

        var element = type.IsArray
            ? type.GetElementType()
            : type.GetInterfaces().Append(type)
                .Where(candidate => candidate.IsGenericType && (candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>)))
                .Select(candidate => candidate.GetGenericArguments()[0])
                .FirstOrDefault();

        if (element is null) { return null; }
        if (element == typeof(string)) { return null; }                  // no member of an element to name
        if (!(element.IsClass || element.IsInterface)) { return null; }   // a dictionary's pairs, and every value type
        if (IsContainer(element)) { return null; }                        // a list of lists, which nothing can reach into

        return element;
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
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
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
    /// <para>
    /// Properties are returned in path order.
    /// </para>
    /// <para>
    /// Given somewhere to put them, a collection of objects is <b>also</b> gathered as a
    /// <see cref="CollectionBindingRequest"/>, its elements resolved from nothing to
    /// <paramref name="collectionDepth"/>. Also, not instead: the property binding is what answers a null test
    /// and an index, and the collection binding is what answers the quantifiers, and they are three questions
    /// about one thing. That depth is a separate budget from <paramref name="maxDepth"/>, since how far into an
    /// element is worth going has nothing to do with how far into the entity it was found.
    /// </para>
    /// </remarks>
    /// <param name="bindings">the list being built, added to in place</param>
    /// <param name="type">the type to walk</param>
    /// <param name="depth">levels already descended, so 0 for the entity itself</param>
    /// <param name="maxDepth">how far down to go, already bounded by the caller</param>
    /// <param name="prefix">the path so far, empty at the top, which is what makes the keys dotted</param>
    /// <param name="settings"></param>
    /// <param name="ancestors">the types found on the path to here, used for loop-checking
    /// </param>
    /// <param name="collections">
    /// [OPT] where to put the collections found, added to in place. Null gathers none, which is what a caller
    /// wanting only the property list asks for
    /// </param>
    /// <param name="collectionDepth">[OPT] how far into an element to go, 0 being the element's own properties</param>
    /// <returns>the same list, for the caller that started it</returns>
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    internal static IReadOnlyList<BindingRequest> ResolveBindables(List<BindingRequest> bindings, Type type, int depth, int maxDepth, string prefix, BindingResolutionSettings settings, HashSet<Type> ancestors, List<CollectionBindingRequest>? collections = null, int collectionDepth = 0)
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

                    // And a second entry where it is a collection worth quantifying over, the key answering both
                    if (collections is not null) { Elements(property.PropertyType, settings, collectionDepth, pathName, collections); }

                    // if we haven't bottomed out, and settings say the property should be expanded
                    if ((depth < maxDepth) && (ShouldExpandType(property.PropertyType, settings, pathName, ancestors)))
                    {
                        ResolveBindables(bindings, property.PropertyType, depth + 1, maxDepth, pathName, settings, ancestors, collections, collectionDepth);
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

    /// <summary>
    /// Record what may be asked about one element of a collection, where the property is one worth quantifying
    /// over.
    /// </summary>
    /// <remarks>
    /// The element is walked from its own root rather than from the entity's, so the paths inside are the
    /// element's and any <see cref="BindingResolutionSettings.IgnorePaths"/> matched here are element-relative.
    /// Loop checking starts fresh for the same reason, the depth given being what bounds it.
    /// </remarks>
    /// <param name="type">the property's declared type</param>
    /// <param name="settings"></param>
    /// <param name="collectionDepth">how far into an element to go, 0 being the element's own properties</param>
    /// <param name="path">the path to the collection, which becomes the key</param>
    /// <param name="found">the list being built, added to in place</param>
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    private static void Elements(Type type, BindingResolutionSettings settings, int collectionDepth, string path, List<CollectionBindingRequest> found)
    {
        if (ElementTypeOf(type) is not { } element) { return; }
        if (ShouldIgnoreType(element, settings)) { return; }

        var bindings = ResolveBindables(new List<BindingRequest>(), element, 0, collectionDepth, "", settings, new HashSet<Type>());

        // Nothing to name inside is nothing to quantify over, and BindCollection refuses an empty set anyway
        if (bindings.Count > 0) { found.Add(new(path, KeyFor(path), element, bindings)); }
    }
}
