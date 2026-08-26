namespace Weequery;

/// <summary>
/// One sort clause. Several apply in the order given, each breaking ties in the one before, and the field must be
/// bound, the same as a field in a condition. See <see cref="Inquiry{T}.ApplySort"/>.
/// </summary>
/// <param name="Field">the binding key to sort on, matched without regard to case</param>
/// <param name="Direction">which way round</param>
public record Sort(string Field, SortDirection Direction);