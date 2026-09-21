using System.Reflection;
using Weequery.Builders;

namespace Weequery;

/// <summary>
/// Walks a type and works out what could be bound on it, which is what
/// <see cref="Inquiry{T}.ResolveBindables(int, BindingResolutionSettings)"/> hands back and
/// <see cref="Inquiry{T}.BindResolve"/> then binds.
/// </summary>
/// <remarks>
/// <para>
/// Its own class because it is its own job: everything here is about reflection over a CLR type, and none of it
/// touches a query, a binding or an Inquiry's state. What comes out is a list of <see cref="BindingRequest"/>,
/// which is data, and what does something with that list is somebody else's problem.
/// </para>
/// <para>
/// Read the warning on <see cref="Inquiry{T}.ResolveBindables(int, BindingResolutionSettings)"/> before using any
/// of this. Resolving a model is the allow-list saying yes to everything, which is a decision rather than a
/// shortcut.
/// </para>
/// </remarks>
internal static class BindingResolver
{
    /// <summary>
    /// Whether a property's type is one the caller asked to leave out, see
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
    /// Whether a type holds anything worth walking into, which a container does not.
    /// </summary>
    /// <remarks>
    /// An array, a List, a Dictionary, anything a foreach would walk. What is *inside* one is not reachable from
    /// here, since this library does not filter into a collection, so all expansion yields is the container's own
    /// bookkeeping: Length, LongLength, Rank, SyncRoot, IsFixedSize, Count, Capacity. None of that is a question
    /// anyone meant to ask, and a provider will refuse to translate most of it. A string is one of these too,
    /// which is why Name.Length is not a key.
    /// </remarks>
    /// <param name="type"></param>
    /// <returns></returns>
    internal static bool IsContainer(Type type)
    {
        // Arrays are covered by this too, every one of them implementing it
        return type.IsAssignableTo(typeof(System.Collections.IEnumerable));
    }

    /// <summary>
    /// Whether the walk should descend into a property, having already decided to bind it.
    /// </summary>
    /// <param name="type">the property's declared type</param>
    /// <param name="settings"></param>
    /// <param name="path">the property's whole path, which is what the ignore rules are matched against</param>
    /// <param name="ancestors">
    /// the types already open on the way here, see <see cref="Inquiry{T}.ResolveBindables(int, BindingResolutionSettings)"/>
    /// </param>
    /// <returns></returns>
    internal static bool ShouldExpandType(Type type, BindingResolutionSettings settings, string path, HashSet<Type> ancestors)
    {
        // a container holds its elements, which are not reachable, and its own bookkeeping, which is noise
        if (IsContainer(type)) { return false; }
        // the property has potential properties of its own. An interface counts: what it promises is reachable
        // through it, and a model that navigates by interface would otherwise resolve nothing below it
        if (!(type.IsClass || type.IsInterface)) { return false; }
        // if we have been directed to ignore child properties for this path
        if (settings.IgnorePaths.Contains($"{path}.")) { return false; }
        // if we have been directed not to expand this type
        if (settings.DoNotExpandTypes.Contains(type)) { return false; }
        // if this type is already open further up the same path, which is a cycle
        if (ancestors.Contains(type)) { return false; }

        return true;
    }

    /// <summary>
    /// The properties of a type that resolution will consider, before any of the settings are applied.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Readable, and not an indexer: an indexer has no path to bind, so taking one would refuse the whole model
    /// when it came to be bound.
    /// </para>
    /// <para>
    /// Names are made distinct, which matters twice. GetProperties on an interface returns what that interface
    /// declares and nothing it inherits, so the inherited ones are gathered separately and two interfaces may
    /// well promise the same name. And a derived class that shadows a property with <c>new</c> reports both.
    /// Either way a path can only mean one thing, so the first wins.
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
    /// The key a resolved path is bound under, which is the path itself unless the language has claimed it.
    /// </summary>
    /// <remarks>
    /// A property named after an operator makes a key that a query could not tell from the operator, so binding
    /// it as it stands is refused, see <see cref="WeequeryException.ThrowIfNotBindingKey"/>. That is the right
    /// answer for a key someone chose and the wrong one for a whole model, which would otherwise resolve to
    /// nothing because one property happens to be called Contains. An underscore is a legal key character and no
    /// operator ends in one, so the suffix is always enough, and it is applied only where it is needed rather
    /// than to every key.
    /// <para>
    /// Only a whole key can collide: a nested "Lair.Contains" is not the operator to begin with, since the
    /// tokenizer reads a dotted path as one word.
    /// </para>
    /// </remarks>
    /// <param name="path">the resolved property path</param>
    /// <returns>the path, or the path with an underscore where the path is a reserved word</returns>
    internal static string KeyFor(string path)
    {
        return QueryKeywords.IsReserved(path) ? $"{path}_" : path;
    }

    /// <summary>
    /// Walk one level of a type, adding a request for every readable property that survives the settings, and
    /// recursing into the ones that have properties of their own.
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
    /// <param name="ancestors">
    /// the types open on the path to here, which is what stops a cycle. A model where two types refer to each
    /// other has no bottom, and the walk would otherwise only be stopped by <paramref name="maxDepth"/>, one
    /// level of which multiplies the paths rather than adding to them
    /// </param>
    /// <returns>the same list, for the caller that started it</returns>
    internal static IReadOnlyList<BindingRequest> ResolveBindables(List<BindingRequest> bindings, Type type, int depth, int maxDepth, string prefix, BindingResolutionSettings settings, HashSet<Type> ancestors)
    {
        // Open on the way in and closed on the way out, so the set is what is above this point on this path
        // rather than everything the walk has ever seen. Two properties of the same type are both expanded; the
        // same type twice down one chain is not.
        ancestors.Add(type);

        try
        {
            var properties = ReadableProperties(type).OrderBy(prop => prop.Name);
            foreach (var property in properties)
            {
                var pathName = (string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}.{property.Name}");

                // A value type with no builder cannot be bound at all, and taking it would refuse the whole model
                // over one property of a struct nobody meant to filter on. Skipped the way an indexer is.
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
