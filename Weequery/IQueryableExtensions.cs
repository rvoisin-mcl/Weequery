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
    /// <param name="settings">
    /// [OPT] what this query decides for itself, see <see cref="InquirySettings"/>. The defaults where none are
    /// given, which is what every query took before there was anything to give
    /// </param>
    /// <returns>A Inquiry{T} for fluent configuration</returns>
    public static Inquiry<T> WithWeequery<T>(this IQueryable<T> query, InquirySettings? settings = null) where T : class
    {
        return new Inquiry<T>(query) { Settings = settings ?? InquirySettings.Default };
    }
}
