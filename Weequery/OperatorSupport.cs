namespace Weequery;

/// <summary>
/// Which operators the thing that will run this query can actually run. Set on
/// <see cref="InquirySettings.Operators"/>.
/// </summary>
/// <remarks>
/// <para>
/// The allow-list says which <i>fields</i> a caller may name. This says which <i>operators</i> they may use on
/// them, and it exists because those are two different questions and only the first one used to be asked.
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
/// <b>It is checked where every other refusal is checked</b>, so <see cref="Inquiry{T}.Build"/> throws
/// <see cref="WeequeryError.NotTranslatable"/> and <see cref="Inquiry{T}.Validate()"/> reports the same thing
/// rather than raising it. A caller filling in a filter box is told which operator they cannot have, beside the
/// box they typed it in, instead of meeting a provider exception later.
/// </para>
/// <para>
/// <b>Everything is supported by default</b>, which is the behaviour this library has always had. A query is
/// only ever refused here by a model that said what its backend cannot do.
/// </para>
/// <para>
/// <b>It counts every operator a condition uses</b>, not only the comparisons: <see cref="Operator.And"/>,
/// <see cref="Operator.Or"/> and <see cref="Operator.Not"/> are operators, the quantifiers are operators, and
/// the ones inside a quantifier count as much as the ones outside it, because whatever runs the query has to
/// run all of them. So <see cref="Supporting(Operator[])"/> is a literal list, and has to name the
/// conjunctions it wants. <see cref="Without(Operator[])"/> is usually what was meant: a backend is almost
/// always everything, minus the two or three things it cannot do.
/// </para>
/// </remarks>
public sealed class OperatorSupport : IEquatable<OperatorSupport>
{
    /// <summary>
    /// Every operator there is, which is the set to compare a partial one against
    /// </summary>
    private static readonly Operator[] All = Enum.GetValues<Operator>();

    /// <summary>
    /// Everything is supported, which is what an Inquiry has unless it is told otherwise
    /// </summary>
    public static OperatorSupport Everything { get; } = new(All);

    /// <summary>
    /// What may be used, which is what <see cref="Allows"/> answers from
    /// </summary>
    public IReadOnlySet<Operator> Supported { get; }

    private OperatorSupport(IEnumerable<Operator> supported)
    {
        Supported = supported.ToHashSet();
    }

    /// <summary>
    /// Support exactly these and nothing else.
    /// </summary>
    /// <remarks>
    /// A literal list, so it has to name <see cref="Operator.And"/>, <see cref="Operator.Or"/> and
    /// <see cref="Operator.Not"/> where conditions will be combined or negated, and the quantifiers where they
    /// will be quantified. Reach for <see cref="Without(Operator[])"/> where the backend does nearly
    /// everything, which is the usual case.
    /// </remarks>
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
    /// <remarks>
    /// <inheritdoc cref="Supporting(Operator[])" path="/remarks"/>
    /// </remarks>
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
    /// The one to reach for. A backend is a backend, so it is almost always everything minus the two or three
    /// things it cannot do, and naming those is both shorter and honest about what is being said.
    /// </remarks>
    /// <param name="operators">what to refuse; duplicates are fine and an empty set refuses nothing</param>
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
