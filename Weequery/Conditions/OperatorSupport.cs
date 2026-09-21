namespace Weequery;

/// <summary>
/// What operators are supported by the query. Set on <see cref="InquirySettings.Operators"/>.
/// </summary>
/// <remarks>
/// <para>
/// This says which <i>operators</i> callers may use, as support may vary based on different backends.
/// <see cref="Operator.IsMatch"/> against SQL Server is the standing example: the field is bound, the condition
/// parses, the expression builds, and the provider refuses it when the query finally runs, a long way from the
/// request that named it.
/// </para>
/// <code>
/// query.WithWeequery(InquirySettings.Default with
/// {
///     Operators = OperatorSupport.Without(Operator.IsMatch, Operator.DoesNotMatch),
/// })
/// </code>
/// <para>
/// <b>Everything is supported by default</b>
/// </para>
/// <para>
/// <b>Every potential operator is checked</b>, not only the tests: <see cref="Operator.And"/>,
/// <see cref="Operator.Or"/> and <see cref="Operator.Not"/> are operators, the quantifiers are operators, and
/// the ones inside a quantifier count as much as the ones outside it, because whatever runs the query has to
/// run all of them. So <see cref="Supporting(Operator[])"/> is a literal list, and has to name the
/// conjunctions it wants. <see cref="Without(Operator[])"/> is usually what was desired, everything except.
/// </para>
/// </remarks>
public sealed class OperatorSupport : IEquatable<OperatorSupport>
{
    /// <summary>
    /// Every operator
    /// </summary>
    private static readonly Operator[] All = Enum.GetValues<Operator>();

    /// <summary>
    /// Everything is supported, the default case
    /// </summary>
    public static OperatorSupport Everything { get; } = new(All);

    /// <summary>
    /// What may be used
    /// </summary>
    public IReadOnlySet<Operator> Supported { get; }

    private OperatorSupport(IEnumerable<Operator> supported)
    {
        Supported = supported.ToHashSet();
    }

    /// <summary>
    /// Support only these and nothing else.
    /// </summary>
    /// <param name="operators">the complete set; order and duplicates do not matter, and an empty set supports nothing</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">null</exception>
    public static OperatorSupport Supporting(params Operator[] operators)
    {
        return Supporting((IEnumerable<Operator>)operators);
    }

    /// <summary>
    /// <inheritdoc cref="Supporting(Operator[])" path="/summary"/>
    /// </summary>
    /// <param name="operators"><inheritdoc cref="Supporting(Operator[])" path="/param[@name='operators']"/></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">null</exception>
    public static OperatorSupport Supporting(IEnumerable<Operator> operators)
    {
        WeequeryException.ThrowIfNull(operators);

        return new OperatorSupport(operators);
    }

    /// <summary>
    /// Support everything except these.
    /// </summary>
    /// <remarks>
    /// The common case, support everything except X/Y/Z
    /// </remarks>
    /// <param name="operators">what to refuse; duplicates are ignored, and an empty set refuses nothing</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">null</exception>
    public static OperatorSupport Without(params Operator[] operators)
    {
        return Without((IEnumerable<Operator>)operators);
    }

    /// <summary>
    /// <inheritdoc cref="Without(Operator[])" path="/summary"/>
    /// </summary>
    /// <remarks>
    /// <inheritdoc cref="Without(Operator[])" path="/remarks"/>
    /// </remarks>
    /// <param name="operators"><inheritdoc cref="Without(Operator[])" path="/param[@name='operators']"/></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">null</exception>
    public static OperatorSupport Without(IEnumerable<Operator> operators)
    {
        WeequeryException.ThrowIfNull(operators);

        var refused = operators.ToHashSet();

        return new OperatorSupport(All.Where(op => !refused.Contains(op)));
    }

    /// <summary>
    /// If this operator may be used
    /// </summary>
    /// <param name="op"></param>
    /// <returns></returns>
    public bool Allows(Operator op)
    {
        return Supported.Contains(op);
    }

    /// <summary>
    /// If everything is supported, which is the set that can never refuse a query
    /// </summary>
    public bool IsEverything { get { return Supported.Count == All.Length; } }

    /// <summary>
    /// Two sets holding the same operators are the same set, however each was arrived at
    /// </summary>
    /// <param name="other"></param>
    /// <returns></returns>
    public bool Equals(OperatorSupport? other)
    {
        return (other is not null) && ((ReferenceEquals(this, other)) || Supported.SetEquals(other.Supported));
    }

    /// <summary>
    /// <inheritdoc cref="Equals(OperatorSupport)" path="/summary"/>
    /// </summary>
    /// <param name="obj"></param>
    /// <returns></returns>
    public override bool Equals(object? obj)
    {
        return Equals(obj as OperatorSupport);
    }

    /// <summary>
    /// Over the members rather than the reference, so it agrees with <see cref="Equals(OperatorSupport)"/>
    /// </summary>
    /// <returns></returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();

        // The members in a fixed order, since a set has none of its own
        foreach (var op in All.Where(Allows)) { hash.Add(op); }

        return hash.ToHashCode();
    }

    /// <summary>
    /// What is supported, or what is missing where that is the shorter half
    /// </summary>
    /// <returns></returns>
    public override string ToString()
    {
        if (IsEverything) { return "every operator"; }
        if (Supported.Count == 0) { return "no operators"; }

        return (Supported.Count > (All.Length / 2))
            ? $"every operator except {string.Join(", ", All.Where(op => !Allows(op)))}"
            : string.Join(", ", All.Where(Allows));
    }
}
