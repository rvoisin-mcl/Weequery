namespace Weequery.Interfaces;

/// <summary>
/// Intended to be paired with <see cref="ICondition"/>, indicates that the condition requires a bound property
/// </summary>
public interface IBound
{
    /// <summary>
    /// Name of bound property
    /// </summary>
    string Field { get; }

    /// <summary>
    /// Which element of the bound collection this tests, or null where the binding is tested as it stands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written as text and read against the collection's key type when the query is built, the same way a value
    /// is: a position for a list or an array, a key for a dictionary. In the query language it is the brackets
    /// after the field, <c>Tallies[apples]</c>.
    /// </para>
    /// <para>
    /// An element that is not there is not an error and not a default: it is the absence of a value, and behaves
    /// exactly as a <see cref="Nullable{T}"/> does, so it satisfies nothing except IsNull and is not caught by
    /// the negative operators either. See <see cref="Operator"/> for what that means.
    /// </para>
    /// <para>
    /// Defaulted, so a condition written before indexing existed, or one a caller implements itself, is a
    /// condition on the binding as a whole.
    /// </para>
    /// </remarks>
    string? Index => null;
}
