namespace Weequery;

/// <summary>
/// What a binding may be used for.
/// </summary>
/// <remarks>
/// <para>
/// A binding grants three separable things, and they are not always wanted together. Filtering lets a caller ask
/// a question of every row; sorting lets them order by it; projecting lets them read it back. A column can be
/// worth returning without being worth interrogating, and one can be worth filtering on without being worth
/// showing.
/// </para>
/// <code>
/// .BindProperty(minion =&gt; minion.Name)                                       // all three, the default
/// .BindProperty(minion =&gt; minion.Notes, "Notes", BindingUse.Projection)      // read it back, and nothing else
/// .BindProperty(minion =&gt; minion.TenantID, "Tenant", BindingUse.Condition)   // filter on it, never show it
/// </code>
/// <para>
/// This is a permission rather than a fact about the type. A property with no ordering of its own is refused for
/// sorting whatever this says, see <see cref="Binding{TClass}.IsOrderable"/>, and a constant is refused
/// for sorting because there is nothing per row to order by.
/// </para>
/// </remarks>
[Flags]
public enum BindingUse
{
    /// <summary>
    /// Bound but usable for nothing
    /// </summary>
    None = 0,

    /// <summary>
    /// May be used in a condition
    /// </summary>
    Condition = 1,

    /// <summary>May be sorted on, see <see cref="Inquiry{T}.ApplySorts(IEnumerable{Sort}?)"/></summary>
    Sort = 2,

    /// <summary>May be read back, see <see cref="Inquiry{T}.BuildProjected"/></summary>
    Projection = 4,

    /// <summary>
    /// All three, which is what the default binding granted unless specified otherwise.
    /// </summary>
    All = Condition | Sort | Projection,
}
