using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Text.RegularExpressions;
using Weequery.Bindings;
using Weequery.Interfaces;

namespace Weequery;

/// <summary>
/// Wrapper class that provides fluent configuration for IQueryable with Weequery
/// </summary>
/// <remarks>
/// <para>
/// The bound properties are the allow-list: a condition or a sort naming against a field with no binding is refused 
/// or dropped.
/// </para>
/// <para>
/// Inquiry is Immutable, which means a configured one can be kept and branched, and neither branch can reach the other:
/// <code>
/// var bound = query.WithWeequery().BindProperties(MinionBindings);
/// var active = bound.ApplyCondition("IsActive = true").Build();
/// var paid   = bound.ApplyCondition("Pay &gt; 10000").Build();    // paid, and only paid
/// </code>
/// Nothing accumulates across the two, because there is nothing they share to accumulate in.
/// </para>
/// <para>
/// <b>What *IS* copied is the lists, not what is in them.</b> A binding is immutable once built and is shared
/// rather than rebuilt, so a copy costs a dictionary and two lists and no more.
/// </para>
/// <para>
/// The one outlier to this is <see cref="DroppedFields"/>, which reports on the build that
/// filled it rather than forming part of the configuration, see <see cref="Build"/>.
/// </para>
/// </remarks>
/// <typeparam name="T"></typeparam>
public partial class Inquiry<T> where T : class
{
    private IQueryable<T> Query { get; init; }

    /// <summary>
    /// The "x" every accessor hangs off, one per entity type rather than one per query.
    /// <para>
    /// All the bindings used together have to share it: a lambda is built from one binding's parameter and a body
    /// assembled from several, so accessors rooted in different parameters would not compose. Sharing it for the
    /// whole type is what lets a binding built once be used by every query after it, see
    /// <see cref="BindingSetCache{T}"/>, and costs nothing to do: an expression tree is immutable, and a parameter is an
    /// identity rather than a value, so two lambdas built over the same one are still two independent lambdas.
    /// </para>
    /// </summary>
    private static readonly ParameterExpression SharedBindingParameter = Expression.Parameter(typeof(T));

    private Dictionary<string, Binding<T>> Bindings { get; init; } = BindingLookup.Create<T>();

    /// <summary>
    /// Bound collections, seperate from bound properties
    /// </summary>
    private Dictionary<string, ICollectionBinding<T>> Collections { get; init; } = new(BindingLookup.KeyComparer);

    private List<ICondition> Conditions { get; init; } = new();
    private List<Sort> Sorts { get; init; } = new();

    /// <summary>
    /// Which fields <see cref="BuildProjected"/> reads back, see <see cref="ApplyProjection(string?, QueryStyle)"/>. Default empty will return
    /// every bound field.
    /// </summary>
    private Projection Projected { get; set; } = Projection.None;

    /// <summary>
    /// The projection a caller applied, or <see cref="Projection.None"/> where none was, see
    /// <see cref="ApplyProjection(string?, QueryStyle)"/>.
    /// </summary>
    /// <remarks>
    /// A projection builder will need to query this to determine what to retain.
    /// </remarks>
    public Projection AppliedProjection { get { return Projected; } }

    /// <summary>
    /// What <see cref="PageSize"/> holds where no size has been named, so that
    /// <see cref="InquirySettings.DefaultPageSize"/> is the one that decides. Not zero, which would read as a
    /// page holding nothing.
    /// </summary>
    private const int UnsetPageSize = -1;

    private int PageSize { get; set; } = UnsetPageSize;
    private int Page { get; set; } = -1;

    private List<DroppedField> Dropped { get; init; } = new();

    /// <summary>
    /// What the last build dropped from the query, see <see cref="InquirySettings.IgnoreUnboundFields"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Empty unless dropping was asked for, since nothing is dropped otherwise. Filled in by whichever of
    /// <see cref="Build"/>, <see cref="BuildPaged"/>, <see cref="BuildProjected"/> and
    /// <see cref="BuildPagedProjected"/> was called, so it describes the last built rather than accumulating
    /// </para>
    /// <code>
    /// var rows = inquiry.Build().ToList();
    ///
    /// if (inquiry.DroppedFields.Count > 0)
    /// {
    ///     // "Filter on 'Gizmo' no longer applies and has been removed from your saved view"
    ///     Warn(inquiry.DroppedFields);
    /// }
    /// </code>
    /// <para>
    /// A field named in two places is reported once for each, so a query filtering and sorting on the same
    /// missing key gives two entries. One named twice in the same place is reported once.
    /// </para>
    /// </remarks>
    public IReadOnlyList<DroppedField> DroppedFields { get { return Dropped; } }

    /// <summary>
    /// Mark a field as dropped, if it has not already been noted for the given use
    /// </summary>
    /// <remarks>
    /// We are unlikely to care if it is dropped from the query twice, but we might care if it was dropped from the 
    /// query AND the sort.
    /// </remarks>
    private void Drop(string field, BindingUse from, string reason = DroppedField.Unbound)
    {
        field = BindingLookup.SplitIndex(field).Key; // A index would be meaningless here

        if (Dropped.Any(entry => (entry.From == from) && BindingLookup.KeyComparer.Equals(entry.Field, field))) { return; }

        Dropped.Add(new DroppedField(field, from, reason));
    }

    /// <summary>
    /// Configurable timeout for <see cref="Operator.IsMatch"/>. One second by default; assign to change it, or
    /// <see cref="Regex.InfiniteMatchTimeout"/> to remove the bound.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A value that exceeds it will raise <see cref="RegexMatchTimeoutException"/> from wherever the query is being
    /// enumerated, instead of a <see cref="WeequeryException"/>
    /// </para>
    /// <para>
    /// This bounds the match only when it is run in-memory Weequery runs it; via EF the databased limits will apply
    /// </para>
    /// </remarks>
    [SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Per entity type is the point: the bound is one per T and the builders close over T's bindings, so Inquiry<T> is where a caller already is when it needs them.")]
    public static TimeSpan MatchTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Any evaluation tuning knobs for this query <see cref="InquirySettings"/>. 
    /// </summary>
    public InquirySettings Settings { get; internal set; } = InquirySettings.Default;

    internal Inquiry(IQueryable<T> query)
    {
        Query = query;
    }

    /// <summary>
    /// Return a cloned copy of this Inquiry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What is copied is the lists, not what is in them.</b> A binding is immutable once built and is shared
    /// rather than rebuilt. Adding a binding or condition to the copy leaves the original's set alone.
    /// </para>
    /// </remarks>
    /// <returns>a clone, sharing nothing mutable</returns>
    private Inquiry<T> Copy()
    {
        return new Inquiry<T>(Query)
        {
            Bindings = new Dictionary<string, Binding<T>>(Bindings, BindingLookup.KeyComparer),
            Collections = new Dictionary<string, ICollectionBinding<T>>(Collections, BindingLookup.KeyComparer),
            Conditions = [.. Conditions],
            Sorts = [.. Sorts],
            Projected = Projected,
            PageSize = PageSize,
            Page = Page,
            Settings = Settings,
        };
    }

}
