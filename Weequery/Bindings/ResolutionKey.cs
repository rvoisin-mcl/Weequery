namespace Weequery.Bindings;

/// <summary>
/// What a <see cref="Inquiry{T}.BindResolve"/> call was asked for, as a value, so that two calls asking for the
/// same thing find the same cached set, see <see cref="BindingSetCache{T}.ForResolution"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BindingResolutionSettings"/> is a record, but its members are sets, and a set compares by
/// reference: two settings written out identically are two different settings as far as the record is concerned.
/// This compares what is in them instead.
/// </para>
/// <para>
/// <b>The sets are copied on the way in</b>, so a caller changing theirs after the call cannot change what an
/// entry was stored under. Paths are compared as binding keys are, without regard to case; types by identity.
/// </para>
/// </remarks>
internal sealed class ResolutionKey : IEquatable<ResolutionKey>
{
    private readonly int maxDepth;
    private readonly BindingUse use;
    private readonly bool ignoreTypeWhenAssignable;
    private readonly HashSet<string> ignorePaths;
    private readonly HashSet<Type> ignoreTypes;
    private readonly HashSet<Type> doNotExpandTypes;
    private readonly HashSet<Type> ignoreAttributes;
    private readonly int hash;

    /// <summary>
    /// ctor
    /// </summary>
    /// <param name="maxDepth">the depth, already bounded by the caller, so 20 and 16 are one key</param>
    /// <param name="settings">the settings, already defaulted by the caller, so null and Default are one key</param>
    /// <param name="use">what the resolved bindings may be used for</param>
    internal ResolutionKey(int maxDepth, BindingResolutionSettings settings, BindingUse use)
    {
        this.maxDepth = maxDepth;
        this.use = use;
        ignoreTypeWhenAssignable = settings.IgnoreTypeWhenAssignable;
        ignorePaths = settings.IgnorePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        ignoreTypes = settings.IgnoreTypes.ToHashSet();
        doNotExpandTypes = settings.DoNotExpandTypes.ToHashSet();
        ignoreAttributes = settings.IgnoreAttributes.ToHashSet();

        hash = HashCode.Combine(maxDepth, use, ignoreTypeWhenAssignable,
            SetHash(ignorePaths, StringComparer.OrdinalIgnoreCase), SetHash(ignoreTypes, EqualityComparer<Type>.Default), SetHash(doNotExpandTypes, EqualityComparer<Type>.Default),
            SetHash(ignoreAttributes, EqualityComparer<Type>.Default));
    }

    /// <summary>
    /// A hash that does not depend on the order a set happens to enumerate in
    /// </summary>
    private static int SetHash<TItem>(HashSet<TItem> set, IEqualityComparer<TItem> comparer) where TItem : notnull
    {
        var combined = set.Count;
        foreach (var item in set) { combined += comparer.GetHashCode(item); }
        return combined;
    }

    public bool Equals(ResolutionKey? other)
    {
        if (other is null) { return false; }
        if (ReferenceEquals(this, other)) { return true; }

        return (hash == other.hash)
            && (maxDepth == other.maxDepth)
            && (use == other.use)
            && (ignoreTypeWhenAssignable == other.ignoreTypeWhenAssignable)
            && ignorePaths.SetEquals(other.ignorePaths)
            && ignoreTypes.SetEquals(other.ignoreTypes)
            && doNotExpandTypes.SetEquals(other.doNotExpandTypes)
            && ignoreAttributes.SetEquals(other.ignoreAttributes);
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as ResolutionKey);
    }

    public override int GetHashCode()
    {
        return hash;
    }
}
