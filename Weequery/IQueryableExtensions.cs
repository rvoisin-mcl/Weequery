namespace Weequery;

/// <summary>
/// IQueryable extension methods
/// </summary>
public static class IQueryableExtensions
{
    /// <summary>
    /// Starts a fluent configuration chain for Weequery
    /// </summary>
    /// <typeparam name="T">The entity type</typeparam>
    /// <param name="query">The IQueryable to configure</param>
    /// <returns>A Inquiry{T} for fluent configuration</returns>
    public static Inquiry<T> WithWeequery<T>(this IQueryable<T> query) where T : class
    {
        return new Inquiry<T>(query);
    }
}
