using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Fop;
using Fop.FopExpression;
using Gridify;
using Microsoft.Extensions.Options;
using Sieve.Models;
using Sieve.Services;

namespace Weequery.Benchmarks;

/// <summary>
/// Weequery against the other libraries that turn a filter string into a query: Sieve, Fop and Gridify.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only where the feature exists in all four.</b> These libraries overlap but do not agree on scope, so the
/// only honest comparison is the intersection: a filter of three terms over three types, a sort, and a page.
/// Weequery's quantifiers, its bound-property operands, its regular expressions, its projections and its
/// translators have no counterpart here and are measured elsewhere; Sieve's custom filter methods and Gridify's
/// compiled-query cache have no counterpart in Weequery. None of that is measured on either side.
/// </para>
/// <para>
/// <b>The same question in four spellings, and the row count is checked before anything is timed.</b> Each
/// library is given the filter its own grammar wants, see the constants below, and <see cref="Agree"/> refuses
/// to let the benchmark run if they select different rows. A filter that quietly matched fewer rows would look
/// faster for it and nothing in a table of timings would say so.
/// </para>
/// <para>
/// <b>Over an in-memory IQueryable, which is the shape all four share.</b> LINQ to Objects compiles the
/// expression tree on every enumeration, so every row here pays that, and it is a large part of what is being
/// measured, as <see cref="FilteringBenchmarks"/> explains at more length. It is paid by all four equally, which
/// is what makes the comparison fair, but it does mean the ratios say less about per row work than the
/// substring numbers in that class do. Against a real provider none of this applies: EF Core caches its plans.
/// </para>
/// <para>
/// <b>Which is why the baseline is a hand written predicate rather than any of the four.</b> It takes no filter
/// string and uses no library, and it pays the same compile as everything below it. That makes the floor
/// visible: a reader can see how much of every number here is the runtime before any library has done anything,
/// and read the distance from it rather than the absolute figure. Without that row a table like this reports a
/// narrow band around 1.00 and invites it to be read as a ranking, when most of what it measures is
/// <c>Expression.Compile</c>.
/// </para>
/// <para>
/// <b>One difference is worth stating rather than measuring.</b> The filter uses Name rather than Alias because
/// Alias is nullable, and given a row holding a null Gridify and Fop raise a NullReferenceException where
/// Weequery and Sieve treat it as not matching. That is a correctness difference rather than a speed one, so it
/// is stated here and kept out of the timings, and what the guard Weequery emits costs is in
/// <see cref="GuardBenchmarks"/>.
/// </para>
/// <para>
/// A note on the exception counter, if it is ever turned on: the Sieve rows throw a handful of exceptions per
/// run and the other three throw none. It is a few per run rather than a few per operation, so it is something
/// caught inside Sieve on a first pass rather than per row, and it does not move the timings. Recorded here so
/// that a reader who sees the column does not take it for a failing benchmark.
/// </para>
/// </remarks>
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[MemoryDiagnoser]
public class ComparisonBenchmarks
{
    private const string FilterCategory = "1. filter";
    private const string PipelineCategory = "2. filter, sort and page";

    /// <summary>Sieve: comma separated terms, _= for starts with</summary>
    private const string SieveFilter = "IsActive==true,Pay>10000,Name_=Al";

    /// <summary>Fop: semicolon separated terms, _= for starts with</summary>
    private const string FopFilter = "IsActive==true;Pay>10000;Name_=Al";

    /// <summary>Gridify: comma separated terms, a single = for equality and ^ for starts with</summary>
    private const string GridifyFilter = "IsActive=true,Pay>10000,Name^Al";

    private const int PageSize = 20;

    /// <summary>Enough rows that the filter has something to do, without the compile drowning everything</summary>
    [Params(1_000)]
    public int Rows { get; set; }

    private IQueryable<Henchman> Queryable = Array.Empty<Henchman>().AsQueryable();
    private SieveProcessor Sieve = null!;

    [GlobalSetup]
    public void Setup()
    {
        Queryable = Workloads.Rows(Rows).AsQueryable();

        // ThrowExceptions so a filter this benchmark got wrong fails loudly rather than filtering nothing
        Sieve = new SieveProcessor(Options.Create(new SieveOptions { ThrowExceptions = true, MaxPageSize = int.MaxValue, DefaultPageSize = int.MaxValue }));

        Agree();
    }

    /// <summary>
    /// The four have to select the same rows, or the times below are being compared on unequal work.
    /// </summary>
    /// <exception cref="InvalidOperationException">they disagree, or the filter matched nothing</exception>
    private void Agree()
    {
        Same(FilterCategory, HandWrittenFilter(), WeequeryFilter(), SieveFilterOnly(), FopFilterOnly(), GridifyFilterOnly());
        Same(PipelineCategory, HandWrittenPipeline(), WeequeryPipeline(), SievePipeline(), FopPipeline(), GridifyPipeline());
    }

    private void Same(string what, params int[] counts)
    {
        if (counts.Distinct().Count() != 1)
        {
            throw new InvalidOperationException($"{what}: the libraries matched different rows ({string.Join(", ", counts)}), so their timings do not compare");
        }

        if (counts[0] == 0)
        {
            throw new InvalidOperationException($"{what}: the filter matched none of the {Rows} rows, so nothing is being measured");
        }
    }

    // ---------- filtering only ----------

    /// <summary>
    /// The floor, and the reason the rest of the table is readable. No filter string and no library: the
    /// predicate is written out, handed to the same in-memory IQueryable, and pays the same
    /// <c>Expression.Compile</c> on enumeration that every row below it pays. Whatever separates a library from
    /// this row is what the library costs; whatever this row costs is the runtime, and it is most of the number.
    /// </summary>
    [BenchmarkCategory(FilterCategory), Benchmark(Baseline = true, Description = "hand written, no library")]
    public int HandWrittenFilter()
    {
        return Queryable
            .Where(henchman => henchman.IsActive && (henchman.Pay > 10000m) && henchman.Name.StartsWith("Al", StringComparison.Ordinal))
            .Count();
    }

    [BenchmarkCategory(FilterCategory), Benchmark(Description = "Weequery: filter")]
    public int WeequeryFilter()
    {
        return Queryable
            .WithWeequery()
            .BindProperties(Workloads.Bindings)
            .ApplyCondition(Workloads.Comparable)
            .Build()
            .Count();
    }

    [BenchmarkCategory(FilterCategory), Benchmark(Description = "Sieve: filter")]
    public int SieveFilterOnly()
    {
        return Sieve.Apply(new SieveModel { Filters = SieveFilter }, Queryable, applySorting: false, applyPagination: false).Count();
    }

    [BenchmarkCategory(FilterCategory), Benchmark(Description = "Fop: filter")]
    public int FopFilterOnly()
    {
        // Fop always pages, so the page is made big enough not to be one
        var (filtered, _) = Queryable.ApplyFop(FopExpressionBuilder<Henchman>.Build(FopFilter, nameof(Henchman.Pay), 1, int.MaxValue));

        return filtered.Count();
    }

    [BenchmarkCategory(FilterCategory), Benchmark(Description = "Gridify: filter")]
    public int GridifyFilterOnly()
    {
        return Queryable.ApplyFiltering(GridifyFilter).Count();
    }

    // ---------- filter, sort and page, which is what all four are actually for ----------

    /// <inheritdoc cref="HandWrittenFilter"/>
    [BenchmarkCategory(PipelineCategory), Benchmark(Baseline = true, Description = "hand written, no library")]
    public int HandWrittenPipeline()
    {
        return Queryable
            .Where(henchman => henchman.IsActive && (henchman.Pay > 10000m) && henchman.Name.StartsWith("Al", StringComparison.Ordinal))
            .OrderByDescending(henchman => henchman.Pay)
            .Take(PageSize)
            .Count();
    }

    [BenchmarkCategory(PipelineCategory), Benchmark(Description = "Weequery: filter, sort and page")]
    public int WeequeryPipeline()
    {
        return Queryable
            .WithWeequery()
            .BindProperties(Workloads.Bindings)
            .ApplyCondition(Workloads.Comparable)
            .ApplySorts(Workloads.ComparableSort)
            .ApplyPagination(PageSize, 0)
            .Build()
            .Count();
    }

    [BenchmarkCategory(PipelineCategory), Benchmark(Description = "Sieve: filter, sort and page")]
    public int SievePipeline()
    {
        var model = new SieveModel { Filters = SieveFilter, Sorts = "-Pay", Page = 1, PageSize = PageSize };

        return Sieve.Apply(model, Queryable).Count();
    }

    [BenchmarkCategory(PipelineCategory), Benchmark(Description = "Fop: filter, sort and page")]
    public int FopPipeline()
    {
        var (paged, _) = Queryable.ApplyFop(FopExpressionBuilder<Henchman>.Build(FopFilter, $"{nameof(Henchman.Pay)};desc", 1, PageSize));

        return paged.Count();
    }

    [BenchmarkCategory(PipelineCategory), Benchmark(Description = "Gridify: filter, sort and page")]
    public int GridifyPipeline()
    {
        var query = new GridifyQuery { Filter = GridifyFilter, OrderBy = "Pay desc", Page = 1, PageSize = PageSize };

        return Queryable.ApplyFilteringOrderingPaging(query).Count();
    }
}
