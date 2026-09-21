namespace Weequery;

/// <summary>
/// What a binding may be used for. Every binding carries one, and it is checked wherever a field is resolved.
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
/// <b>Absent is not the same as refused.</b> A key nobody bound is reported as unbound; a key bound without the
/// use being asked for is reported as bound for something else, and says what. The caller was given the name and
/// it works elsewhere, so telling them it does not exist would send them looking for a typo.
/// </para>
/// <para>
/// This is a permission rather than a fact about the type. A property with no ordering of its own is refused for
/// sorting whatever this says, see <see cref="Weequery.Binding{TClass}.IsOrderable"/>, and a constant is refused
/// for sorting because there is nothing per row to order by.
/// </para>
/// </remarks>
[Flags]
public enum BindingUse
{
    /// <summary>
    /// Bound and usable for nothing, which is a binding that may as well not exist. Here because a flags enum
    /// needs a zero, and because it is what a caller gets from combining no flags at all rather than a surprise.
    /// </summary>
    None = 0,

    /// <summary>
    /// May be named in a condition: on the left of an operator, and as an operand another field is compared
    /// against. The two go together because comparing against a field is a read of it, so granting one and not
    /// the other would leave the value learnable by bisection.
    /// </summary>
    Condition = 1,

    /// <summary>May be sorted on, see <see cref="Inquiry{T}.ApplySorts(IEnumerable{Sort}?)"/></summary>
    Sort = 2,

    /// <summary>May be read back, see <see cref="Inquiry{T}.BuildProjected"/></summary>
    Projection = 4,

    /// <summary>
    /// All three, which is what a binding grants unless it says otherwise. The default everywhere, so a binding
    /// list written before any of this existed means what it always meant.
    /// </summary>
    All = Condition | Sort | Projection,
}
