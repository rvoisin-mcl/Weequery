using Weequery.Elasticsearch;
using Weequery.OData;

namespace Weequery.Benchmarks;

/// <summary>
/// The conditions every benchmark is run against, and the allow-lists they are run through.
/// </summary>
/// <remarks>
/// <para>
/// Shared so that a figure from one benchmark can be read against a figure from another: the parse cost and the
/// build cost of <see cref="Typical"/> are the two halves of one number, and they only add up if both were
/// measuring the same string. The same reason the translators get the same conditions as the expression builder.
/// </para>
/// <para>
/// Three sizes, because the interesting question is not what one condition costs but how the cost moves with the
/// tree. <see cref="Simple"/> is the floor, <see cref="Typical"/> is what a filter bar sends, and
/// <see cref="Complex"/> is a caller who has found the advanced search. If the middle one is cheap and the last
/// one is not, that is worth knowing and would not show up from measuring one.
/// </para>
/// </remarks>
public static class Workloads
{
    /// <summary>One comparison. The floor: whatever this costs is the fixed cost of going through Weequery at all.</summary>
    public const string Simple = "Pay > 10000";

    /// <summary>Three terms and a string operator, which is what a filter bar sends.</summary>
    public const string Typical = "IsActive = true AND Pay > 10000 AND Alias StartsWith 'Al'";

    /// <summary>
    /// Nesting, a disjunction, a list, a range, a null test and a negation. Nothing exotic, just more of it.
    /// </summary>
    public const string Complex =
        "(IsActive = true AND Pay IsBetween (10000, 50000)) OR " +
        "(Clearance IsIn ('Sensitive', 'Doomsday') AND NOT (Alias IsNull) AND HireDate < 2020-01-01)";

    /// <summary>A quantifier over a bound collection, which is the one shape that reaches into another type.</summary>
    public const string Quantified = "Assignments Any (Role = 'Driver' AND Stipend > 500)";

    /// <summary>
    /// Two value typed properties, neither of which can hold a null, so neither is guarded.
    /// </summary>
    /// <remarks>
    /// The three below are for <see cref="GuardBenchmarks"/>, which takes the per row cost apart by what the
    /// property's type forces. See there for why the type rather than the declaration is what decides it.
    /// </remarks>
    public const string Unguarded = "IsActive = true AND Pay > 10000";

    /// <summary>A string that can hold a null, which any honest predicate has to guard, Weequery's or yours.</summary>
    public const string GuardedNullable = "Alias StartsWith 'Al'";

    /// <summary>A string declared non-nullable, which Weequery guards anyway because a reference can be null.</summary>
    public const string GuardedText = "Name StartsWith 'Al'";

    /// <summary>
    /// One string equality, which is a different comparison from a substring match rather than a cheaper one.
    /// </summary>
    /// <remarks>
    /// Here because the rules a comparison follows are the query to pick, see
    /// <see cref="InquirySettings.StringComparison"/>, and equality is where picking them costs the most: the
    /// operator a hand written predicate compiles to is ordinal, and a linguistic comparison of the same two
    /// strings is a different amount of work. Nothing else in these benchmarks runs a string equality in memory,
    /// so without this the choice would go unmeasured.
    /// </remarks>
    public const string Equality = "Name = 'Alice Fox'";

    /// <summary>
    /// The one condition every library in <see cref="ComparisonBenchmarks"/> can express, in Weequery spelling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three terms over three types, which is the shape a filter bar sends and the shape all four of them are
    /// for. It is <see cref="Typical"/> with one difference: the string term is over Name rather than Alias,
    /// because Alias is nullable and Gridify and Fop throw a NullReferenceException on a row holding one rather
    /// than treating it as not matching. Comparing against a filter half of them cannot run would measure
    /// nothing, so the null guard is left to <see cref="GuardBenchmarks"/>, which is about exactly that.
    /// </para>
    /// <para>
    /// The other spellings of the same question are on the benchmarks that use them, since each belongs to its
    /// library rather than here.
    /// </para>
    /// </remarks>
    public const string Comparable = "IsActive = true AND Pay > 10000 AND Name StartsWith 'Al'";

    /// <summary>The sort every library in that comparison is given, in Weequery spelling</summary>
    public const string ComparableSort = "Pay DESC";

    /// <summary>The same, parsed once, for the benchmarks measuring what happens after parsing</summary>
    public static readonly ICondition SimpleCondition = ConditionFunctions.ParseQuery(Simple)!;

    /// <inheritdoc cref="SimpleCondition"/>
    public static readonly ICondition TypicalCondition = ConditionFunctions.ParseQuery(Typical)!;

    /// <inheritdoc cref="SimpleCondition"/>
    public static readonly ICondition ComplexCondition = ConditionFunctions.ParseQuery(Complex)!;

    /// <inheritdoc cref="SimpleCondition"/>
    public static readonly ICondition QuantifiedCondition = ConditionFunctions.ParseQuery(Quantified)!;

    /// <inheritdoc cref="SimpleCondition"/>
    public static readonly ICondition EqualityCondition = ConditionFunctions.ParseQuery(Equality)!;

    /// <inheritdoc cref="SimpleCondition"/>
    public static readonly ICondition ComparableCondition = ConditionFunctions.ParseQuery(Comparable)!;

    /// <summary>
    /// What a caller may ask about. Written out rather than resolved, since resolving is one of the things being
    /// measured and a benchmark should not depend on the thing it is comparing against.
    /// </summary>
    public static readonly BindingRequest[] Bindings =
    [
        new(nameof(Henchman.HenchmanID), null),
        new(nameof(Henchman.Name), null),
        new(nameof(Henchman.Alias), null),
        new(nameof(Henchman.Pay), null),
        new(nameof(Henchman.IsActive), null),
        new(nameof(Henchman.HireDate), null),
        new(nameof(Henchman.FireDate), null),
        new(nameof(Henchman.Clearance), null),
        new(nameof(Henchman.LairID), null),
    ];

    /// <summary>The same allow-list against an index</summary>
    public static readonly ElasticFieldSet ElasticFields = new()
    {
        new("HenchmanID", "henchman_id"),
        new("Name", "name", ElasticFieldKind.Text),
        new("Alias", "alias.keyword"),
        new("Pay", "salary", ElasticFieldKind.Number),
        new("IsActive", "active", ElasticFieldKind.Boolean),
        new("HireDate", "hired_on", ElasticFieldKind.Date),
        new("FireDate", "fired_on", ElasticFieldKind.Date),
        new("Clearance", "clearance"),
        new("LairID", "lair_id", ElasticFieldKind.Number),

        // The collection and what is inside it, all naming the one nested path
        new("Assignments", "assignments", ElasticFieldKind.Keyword, Nested: "assignments"),
        new("Role", "assignments.role", ElasticFieldKind.Keyword, Nested: "assignments"),
        new("Stipend", "assignments.stipend", ElasticFieldKind.Number, Nested: "assignments"),
    };

    /// <summary>The same allow-list against an OData service</summary>
    public static readonly ODataFieldSet ODataFields = new()
    {
        new("HenchmanID", "HenchmanID", ODataFieldKind.Guid),
        new("Name", "Name"),
        new("Alias", "Alias"),
        new("Pay", "Salary", ODataFieldKind.Number),
        new("IsActive", "Active", ODataFieldKind.Boolean),
        new("HireDate", "HiredOn", ODataFieldKind.Date),
        new("FireDate", "FiredOn", ODataFieldKind.Date),
        new("Clearance", "Clearance", ODataFieldKind.Enum),
        new("LairID", "LairID", ODataFieldKind.Number),

        new("Assignments", "Assignments", ODataFieldKind.Collection),
        new("Role", "Role", ODataFieldKind.String, Collection: "Assignments"),
        new("Stipend", "Stipend", ODataFieldKind.Number, Collection: "Assignments"),
    };

    /// <summary>
    /// Rows for the benchmarks that actually filter something, built the same way every run so the numbers are
    /// over the same data.
    /// </summary>
    /// <param name="count"></param>
    /// <returns></returns>
    public static List<Henchman> Rows(int count)
    {
        var random = new Random(20260909);
        string[] firstNames = ["Alice", "Bob", "Charlie", "David", "Edith", "Fred", "Greta", "Hank"];
        string[] lastNames = ["Fox", "Samuelson", "Smith", "Edgars", "Crane", "Wall", "Yoder", "Stone"];
        string[] roles = ["Driver", "Guard", "Technician", "Pilot"];

        List<Henchman> henchmen = new(count);
        for (int i = 0; i < count; i++)
        {
            henchmen.Add(new Henchman
            {
                HenchmanID = Guid.NewGuid(),
                Name = $"{firstNames[random.Next(firstNames.Length)]} {lastNames[random.Next(lastNames.Length)]}",
                Alias = ((i % 5) == 0) ? null : $"Al{i}",
                Pay = random.Next(0, 20000),
                IsActive = (i % 3) != 0,
                HireDate = new DateTime(2010, 1, 1).AddDays(i % 5000),
                FireDate = ((i % 4) == 0) ? new DateTime(2024, 12, 25) : null,
                Clearance = (Clearance)(i % 4),
                LairID = i % 12,
                Assignments =
                [
                    new Assignment { LairID = i % 12, Role = roles[i % roles.Length], Stipend = 100 * (i % 9), IsPrimary = true },
                    new Assignment { LairID = (i + 1) % 12, Role = roles[(i + 1) % roles.Length], Stipend = 50, IsPrimary = false },
                ],
            });
        }

        return henchmen;
    }
}
