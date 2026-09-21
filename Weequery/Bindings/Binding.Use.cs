
namespace Weequery.Bindings;

// What a binding may be used for once it exists: widening and narrowing the grant, and what happens when two
// bindings claim the same key.
internal partial class Binding<TClass>
{
    /// <summary>
    /// This binding with <paramref name="granted"/> added to what it may be used for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A use is only ever added, never taken away, which is what lets an allow-list be declared broadly and then
    /// narrowed to: bind everything for projection, then name the few that may also be filtered on, and the
    /// second call grants rather than replaces. It also means the order of the two calls does not matter.
    /// </para>
    /// <para>
    /// Copied rather than rebuilt. The accessor, the null checks and the inlined converter are expression trees
    /// that were derived once and are immutable, so cloning the object and changing the one field is exact where
    /// running the constructor again would be a re-derivation that has to keep agreeing with the first.
    /// </para>
    /// </remarks>
    /// <param name="granted">what to add, which may be something it already allows</param>
    /// <returns>this, where it already allows all of it</returns>
    internal Binding<TClass> Widened(BindingUse granted)
    {
        return WithUse(Use | granted);
    }

    /// <summary>
    /// This binding with <paramref name="revoked"/> taken off what it may be used for.
    /// </summary>
    /// <remarks>
    /// The other direction, for <see cref="Inquiry{T}.RemoveBinding"/>. A binding narrowed to
    /// <see cref="BindingUse.None"/> is one that can no longer answer anything, which is a binding that should
    /// not be there at all, and removing it is that caller's business rather than this method's.
    /// </remarks>
    /// <param name="revoked">what to take off, which may be something it never allowed</param>
    /// <returns>this, where it allowed none of it</returns>
    internal Binding<TClass> Narrowed(BindingUse revoked)
    {
        return WithUse(Use & ~revoked);
    }

    /// <summary>
    /// This binding, allowing exactly <paramref name="use"/>.
    /// </summary>
    /// <remarks>
    /// Copied rather than rebuilt. The accessor, the null checks and the inlined converter are expression trees
    /// that were derived once and are immutable, so cloning the object and changing the one field is exact where
    /// running the constructor again would be a re-derivation that has to keep agreeing with the first.
    /// </remarks>
    /// <param name="use"></param>
    /// <returns>this, where that is already what it allows</returns>
    private Binding<TClass> WithUse(BindingUse use)
    {
        if (use == Use) { return this; }

        var copy = (Binding<TClass>)MemberwiseClone();

        copy.Use = use;

        return copy;
    }

    /// <summary>
    /// The one binding a key should hold, where two arrived for the same property.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The uses are added together, see <see cref="Widened"/>, and a converter is granted the same way: a
    /// binding that never mentioned one is not asserting that there is none, so the call that names it wins
    /// whichever order the two arrive in. Nothing a bind does takes anything away.
    /// </para>
    /// <para>
    /// <b>Two different converters are the exception</b>, and they are refused. There is no merging TOUPPER with
    /// TOLOWER, and picking one quietly would leave a caller reading values that had been through a conversion
    /// nobody asked for, which is the sort of wrong answer that looks right. Identity rather than equivalence
    /// decides it: a converter wraps a delegate, so two built from the same lambda cannot be shown to agree and
    /// are treated as two.
    /// </para>
    /// </remarks>
    /// <param name="existing">what the key already holds</param>
    /// <param name="candidate">what arrived for it</param>
    /// <param name="key">the key, so the message names what the caller wrote</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">the two carry different converters and neither is null</exception>
    internal static Binding<TClass> Merged(Binding<TClass> existing, Binding<TClass> candidate, string key)
    {
        var use = existing.Use | candidate.Use;

        if (ReferenceEquals(existing.Converter, candidate.Converter)) { return existing.WithUse(use); }

        // Whichever of them named one, since the other did not ask for it to be taken off. The binding carrying
        // the converter is the one kept rather than rebuilt, its accessor already having the conversion inlined
        if (existing.Converter is null) { return candidate.WithUse(use); }
        if (candidate.Converter is null) { return existing.WithUse(use); }

        throw new WeequeryException(WeequeryError.KeyTaken, $"'{key}' is already bound with a different ValueConverter. One key cannot mean two normalisations of the same property, so bind it once with the converter it should have");
    }

    /// <summary>
    /// If a binding arriving under a key that is already taken is for the property already there, so the two
    /// can be merged rather than being a conflict.
    /// </summary>
    /// <remarks>
    /// One property named by both routes into a set is one binding, and refusing the second call would make the
    /// order they were written in matter. A constant is never the same as anything: it carries a value the path
    /// says nothing about, and its path is its own key, so comparing paths alone would make it a duplicate of
    /// whatever property is bound there and hand that property back in its place, losing the value the caller
    /// supplied without saying so.
    /// </remarks>
    /// <param name="existing">the binding already under the key</param>
    /// <param name="candidate">the one arriving</param>
    /// <returns>true where the two are the same binding</returns>
    internal static bool IsSameBinding(Binding<TClass> existing, Binding<TClass> candidate)
    {
        return !existing.IsConstant && !candidate.IsConstant && (existing.PropertyPath == candidate.PropertyPath);
    }

    /// <summary>
    /// Put a binding in the lookup under the key it will be asked for by, if there is a lookup to put it in.
    /// </summary>
    /// <param name="bindings">[OPT] where to add it</param>
    /// <param name="binding"></param>
    /// <param name="useKey">the key given, or the one derived for it</param>
    /// <returns>the binding, added or not</returns>
    /// <exception cref="WeequeryException">the key is not a valid name, or is already taken</exception>
    private static Binding<TClass> AddTo(Dictionary<string, Binding<TClass>>? bindings, Binding<TClass> binding, string useKey)
    {
        if (bindings is not null)
        {
            // Covers the derived key as well as an explicit one, including the one an indexed path would derive
            WeequeryException.ThrowIfNotBindingKey(useKey, "key");

            if (bindings.TryGetValue(useKey, out var existing)) // Keys are case-insensitive
            {
                // The same binding arriving twice is not a conflict, see IsSameBinding for what "the same" means,
                // and the second one's use is added to the first's rather than dropped, see Merged
                if (IsSameBinding(existing, binding))
                {
                    var merged = Merged(existing, binding, useKey);

                    bindings[useKey] = merged;

                    return merged;
                }

                throw new WeequeryException(WeequeryError.KeyTaken, $"Binding already exists for '{useKey}'");
            }

            bindings[useKey] = binding;
        }

        return binding;
    }

}
