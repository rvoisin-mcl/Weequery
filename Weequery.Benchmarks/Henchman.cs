using Sieve.Attributes;

namespace Weequery.Benchmarks;

/// <summary>
/// What the benchmarks filter. Deliberately not the test suite's Minion.
/// </summary>
/// <remarks>
/// <para>
/// A published number has to mean the same thing next quarter as it does today, and half of what these measure
/// is the size of the model: how many bindings get resolved, how many properties a path walks through. Sharing
/// the test model would mean a property added to cover some parsing edge case silently moves every figure in
/// BENCHMARKS.md, and nobody would connect the two. So this one is fixed, and is changed only on purpose.
/// </para>
/// <para>
/// It is still a realistic shape: a mix of the types that get bound in practice, a nullable string so the null
/// guard is exercised, and a collection so the quantifiers have something to quantify over.
/// </para>
/// <para>
/// The <see cref="SieveAttribute"/>s are how Sieve is told what a caller may filter and sort on, and they are
/// here so that <see cref="ComparisonBenchmarks"/> measures Sieve the way Sieve is meant to be used. They say
/// nothing to anything else: Weequery takes its allow-list as a value, see <see cref="Workloads.Bindings"/>, and
/// Gridify and Fop map by property name.
/// </para>
/// </remarks>
public class Henchman
{
    public Guid HenchmanID { get; set; }

    [Sieve(CanFilter = true, CanSort = true)]
    public string Name { get; set; } = "";

    /// <summary>Nullable on purpose: the string operators guard it, which is work the non-nullable ones skip</summary>
    [Sieve(CanFilter = true, CanSort = true)]
    public string? Alias { get; set; }

    [Sieve(CanFilter = true, CanSort = true)]
    public decimal Pay { get; set; }

    [Sieve(CanFilter = true, CanSort = true)]
    public bool IsActive { get; set; }

    [Sieve(CanFilter = true, CanSort = true)]
    public DateTime HireDate { get; set; }

    [Sieve(CanFilter = true, CanSort = true)]
    public DateTime? FireDate { get; set; }

    [Sieve(CanFilter = true, CanSort = true)]
    public Clearance Clearance { get; set; }

    [Sieve(CanFilter = true, CanSort = true)]
    public int LairID { get; set; }

    public List<Assignment>? Assignments { get; set; }
}

/// <summary>An enum, since binding one costs a conversion the primitives do not</summary>
public enum Clearance
{
    None,
    Basic,
    Sensitive,
    Doomsday,
}

/// <summary>What a <see cref="Henchman"/>'s collection holds, so a quantifier has an element to bind against</summary>
public class Assignment
{
    public int LairID { get; set; }

    public string Role { get; set; } = "";

    public decimal Stipend { get; set; }

    public bool IsPrimary { get; set; }
}
