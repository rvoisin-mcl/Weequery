namespace Weequery;

/// <summary>
/// Which way a <see cref="Sort"/> runs. Nullable properties can be sorted on, and will sort nulls first.
/// </summary>
public enum SortDirection
{
    /// <summary>Smallest first</summary>
    Ascending,

    /// <summary>Largest first</summary>
    Descending
}
