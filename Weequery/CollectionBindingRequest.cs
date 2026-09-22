namespace Weequery;

/// <summary>
/// A collection that resolution found, and what may be asked about one of its elements. See
/// <see cref="Inquiry{T}.ResolveBindableCollections"/>.
/// </summary>
/// <remarks>
/// <para>
/// The collection half of what resolution discovers. A <see cref="BindingRequest"/> says a property may be
/// compared; this says a property holds many of something and names what may be asked about one of them, which
/// is what a quantifier needs and a flat list of paths cannot say.
/// </para>
/// <para>
/// The same path comes back from <see cref="Inquiry{T}.ResolveBindables"/> as well, and binding both is the
/// point: one key, answering a null test and an index as a property and the quantifiers as a collection.
/// </para>
/// </remarks>
/// <param name="PropertyPath">the path to the collection on the entity, dotted where it is nested</param>
/// <param name="Key">the name a caller writes the quantifier against</param>
/// <param name="ElementType">what the collection holds</param>
/// <param name="Elements">what may be asked about one element, keyed within the collection</param>
public sealed record CollectionBindingRequest(string PropertyPath, string Key, Type ElementType, IReadOnlyList<BindingRequest> Elements);
