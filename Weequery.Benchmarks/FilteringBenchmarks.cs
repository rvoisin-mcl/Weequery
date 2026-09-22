using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using System.Linq.Expressions;

namespace Weequery.Benchmarks;

/// <summary>
/// Actually filtering rows, against a hand written predicate over the same rows.
/// </summary>
/// <remarks>
/// <para>
/// In memory rather than against a database, deliberately. A database benchmark would mostly measure the
/// database: the test suite already argues the execution case the honest way, by showing that for the comparison
/// operators Weequery emits byte for byte the statement a hand written <c>Where</c> emits, so the server cannot
/// tell them apart. What is left to measure is the work on this side of the wire.
/// </para>
/// <para>
/// <b>The baseline is the compiled lambda over a List, and the comparison that means something is the row above
/// it.</b> Both run a predicate per element with no expression tree in the path, which is the same work, so the
/// ratio between those two is Weequery's actual overhead per row: the null guards it puts in that a hand written
/// predicate leaves out.
/// </para>
/// <para>
/// <b>The three rows using AsQueryable are not a fair comparison and are here to show why.</b> LINQ to Objects
/// compiles the expression tree when it enumerates, and does it again on the next call, reusing the same
/// expression object does not help, and the cost does not move when the row count moves by a factor of a hundred,
/// because it is not per row work. A ratio taken against one of those would say more about
/// <c>Expression.Compile</c> than about this library, and would flatter it: both sides pay the same compile and
/// the difference between them disappears into it. They are useful for one thing only, which is as the argument
/// for <see cref="Inquiry{T}.BuildDelegate"/> over <c>Build</c> when the data is already in memory. Against a
/// real provider none of this applies: EF Core caches its query plans.
/// </para>
/// <para>
/// <b>The hand written predicates spell out StringComparison.Ordinal, and that is not noise.</b> The one argument
/// overloads of StartsWith and EndsWith compare linguistically, and Weequery compares by
/// <see cref="InquirySettings.StringComparison"/>, which is ordinal unless a query says otherwise. Left as
/// <c>StartsWith("Al")</c> the baseline would be doing a different and much more expensive comparison than the
/// row being measured against it, and the ratio would flatter this library rather than measure it. The equality
/// category below keeps both rules as separate rows, because there the difference is the thing being shown.
/// </para>
/// <para>
/// Both row counts run because they answer different questions. At a hundred rows the fixed cost of building
/// dominates; at ten thousand it has been amortised away and what is left is the per row work.
/// </para>
/// </remarks>
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[MemoryDiagnoser]
public class FilteringBenchmarks
{
    private const string SubstringCategory = "1. substring, over a nullable string";
    private const string EqualityCategory = "2. string equality, where the rules are the query's to pick";

    /// <summary>Small enough that building dominates, and large enough that running does</summary>
    [Params(100, 10_000)]
    public int Rows { get; set; }

    private List<Henchman> Henchmen = [];
    private IQueryable<Henchman> Queryable = Array.Empty<Henchman>().AsQueryable();
    private Expression<Func<Henchman, bool>> Prebuilt = henchman => true;
    private Func<Henchman, bool> Compiled = henchman => true;

    private Func<Henchman, bool> Equality = henchman => true;
    private Func<Henchman, bool> EqualityInCulture = henchman => true;

    [GlobalSetup]
    public void Setup()
    {
        Henchmen = Workloads.Rows(Rows);
        Queryable = Henchmen.AsQueryable();
        Prebuilt = Inquiry<Henchman>.BuildExpression(Workloads.Bindings, Workloads.TypicalCondition);
        Compiled = Inquiry<Henchman>.BuildDelegate(Workloads.Bindings, Workloads.TypicalCondition);

        Equality = Inquiry<Henchman>.BuildDelegate(Workloads.Bindings, Workloads.EqualityCondition);
        EqualityInCulture = Inquiry<Henchman>.BuildDelegate(
            Workloads.Bindings,
            Workloads.EqualityCondition,
            InquirySettings.Default with { StringComparison = StringComparison.CurrentCulture });

        Agree();
    }

    /// <summary>
    /// Every approach here has to match the same rows, or the times below are being compared on unequal work.
    /// </summary>
    /// <remarks>
    /// Checked once per parameter set rather than asserted per iteration, which would be measuring the check. It
    /// is here because the failure it catches is silent: a hand written predicate that quietly drops the null
    /// guard filters fewer rows and looks faster for it, and nothing in a table of timings would say so.
    /// </remarks>
    /// <exception cref="InvalidOperationException">the approaches disagree, or the filter matched nothing</exception>
    private void Agree()
    {
        Same(SubstringCategory, HandWrittenList(), WeequeryCompiled(), HandWrittenQueryable(), WeequeryPrebuilt(), WeequeryPerQuery());
        Same(EqualityCategory, HandWrittenEquality(), HandWrittenEqualityInCulture(), WeequeryEquality(), WeequeryEqualityInCulture());
    }

    private void Same(string what, params int[] counts)
    {
        if (counts.Distinct().Count() != 1)
        {
            throw new InvalidOperationException($"{what}: the approaches matched different rows ({string.Join(", ", counts)}), so their timings do not compare");
        }

        if (counts[0] == 0)
        {
            throw new InvalidOperationException($"{what}: the filter matched none of the {Rows} rows, so nothing is being measured");
        }
    }

    // ---------- the comparison that means something: a predicate per element, both sides ----------

    /// <summary>
    /// The floor. A lambda the C# compiler turned into a delegate before the program started.
    /// </summary>
    [BenchmarkCategory(SubstringCategory), Benchmark(Baseline = true, Description = "hand written, compiled lambda")]
    public int HandWrittenList()
    {
        return Henchmen.Count(henchman => henchman.IsActive && (henchman.Pay > 10000m) && (henchman.Alias != null) && henchman.Alias.StartsWith("Al", StringComparison.Ordinal));
    }

    /// <summary>
    /// The same shape, from a condition: what <see cref="Inquiry{T}.BuildDelegate"/> is for, and the right way to
    /// filter a collection already in memory. The distance from the baseline is what the null guards cost.
    /// </summary>
    [BenchmarkCategory(SubstringCategory), Benchmark(Description = "Weequery, compiled delegate")]
    public int WeequeryCompiled()
    {
        return Henchmen.Count(Compiled);
    }

    // ---------- the queryable path, where LINQ to Objects compiles the tree on every call ----------

    /// <summary>
    /// A hand written predicate handed to <c>AsQueryable</c>, which is dominated by the compile rather than by
    /// the rows. Here so that the three rows below it can be read against something.
    /// </summary>
    [BenchmarkCategory(SubstringCategory), Benchmark(Description = "hand written, over AsQueryable")]
    public int HandWrittenQueryable()
    {
        return Queryable
            .Where(henchman => henchman.IsActive && (henchman.Pay > 10000m) && (henchman.Alias != null) && henchman.Alias.StartsWith("Al", StringComparison.Ordinal))
            .Count();
    }

    /// <summary>The predicate built once and kept, which does not help: the provider compiles it again anyway</summary>
    [BenchmarkCategory(SubstringCategory), Benchmark(Description = "Weequery, predicate reused, over AsQueryable")]
    public int WeequeryPrebuilt()
    {
        return Queryable.Where(Prebuilt).Count();
    }

    /// <summary>The whole chain per query, which is how the fluent API reads</summary>
    [BenchmarkCategory(SubstringCategory), Benchmark(Description = "Weequery, built per query, over AsQueryable")]
    public int WeequeryPerQuery()
    {
        return Queryable
            .WithWeequery()
            .BindProperties(Workloads.Bindings)
            .ApplyCondition(Workloads.Typical)
            .Build()
            .Count();
    }

    // ---------- string equality, where the comparison rules cost more than the machinery around them ----------

    /// <summary>
    /// The floor, and the one a caller writing this by hand would get without thinking about it: the equality
    /// operator on two strings is ordinal, whatever culture the process is running in.
    /// </summary>
    [BenchmarkCategory(EqualityCategory), Benchmark(Baseline = true, Description = "hand written, ordinal (the default rules, and what == gives you)")]
    public int HandWrittenEquality()
    {
        return Henchmen.Count(henchman => henchman.Name == "Alice Fox");
    }

    /// <summary>
    /// The same question under a linguistic comparison, written by hand. This is the row that makes
    /// the table honest: without it the whole distance from the baseline would be charged to this library, when
    /// most of it is the comparison itself. What separates this from the baseline is what linguistic comparison
    /// costs; what separates the next row from this one is what Weequery costs.
    /// </summary>
    [BenchmarkCategory(EqualityCategory), Benchmark(Description = "hand written, current culture (what a query can ask for)")]
    public int HandWrittenEqualityInCulture()
    {
        return Henchmen.Count(henchman => string.Equals(henchman.Name, "Alice Fox", StringComparison.CurrentCulture));
    }

    /// <summary>
    /// From a condition, under the default rules, so it answers the same question as the row above by the same
    /// means. The difference is the null guard, as it is in the substring category.
    /// </summary>
    [BenchmarkCategory(EqualityCategory), Benchmark(Description = "Weequery, compiled delegate, default rules")]
    public int WeequeryEquality()
    {
        return Henchmen.Count(Equality);
    }

    /// <summary>
    /// The same condition with the query asking for a culture instead, see
    /// <see cref="InquirySettings.StringComparison"/>. Here because the setting is a performance lever as much as
    /// a correctness one, and this is the row that says what asking costs.
    /// </summary>
    [BenchmarkCategory(EqualityCategory), Benchmark(Description = "Weequery, compiled delegate, culture rules")]
    public int WeequeryEqualityInCulture()
    {
        return Henchmen.Count(EqualityInCulture);
    }
}
