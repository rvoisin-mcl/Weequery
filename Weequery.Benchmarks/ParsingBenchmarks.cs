using BenchmarkDotNet.Attributes;

namespace Weequery.Benchmarks;

/// <summary>
/// Reading a condition out of text, and writing one back into it.
/// </summary>
/// <remarks>
/// The first thing that happens to a filter arriving from a caller, and the only part of the request path that
/// scales with what the caller typed rather than with what was bound. Separate from
/// <see cref="ExpressionBenchmarks"/> on purpose: the two are added together to get the cost of a request, and
/// which of them dominates decides where any effort would be worth spending.
/// </remarks>
[MemoryDiagnoser]
public class ParsingBenchmarks
{
    [Benchmark(Baseline = true, Description = "parse: one comparison")]
    public ICondition? ParseSimple()
    {
        return ConditionFunctions.ParseQuery(Workloads.Simple);
    }

    [Benchmark(Description = "parse: three terms")]
    public ICondition? ParseTypical()
    {
        return ConditionFunctions.ParseQuery(Workloads.Typical);
    }

    [Benchmark(Description = "parse: nested, list, range")]
    public ICondition? ParseComplex()
    {
        return ConditionFunctions.ParseQuery(Workloads.Complex);
    }

    [Benchmark(Description = "parse: quantifier")]
    public ICondition? ParseQuantified()
    {
        return ConditionFunctions.ParseQuery(Workloads.Quantified);
    }

    /// <summary>
    /// The condition and the sort clause in one string, which is the shape a query string actually arrives in.
    /// </summary>
    [Benchmark(Description = "parse: filter and sort together")]
    public ParsedQuery ParseFilterAndSort()
    {
        return ParsedQuery.Parse("IsActive = true AND Pay > 10000 ORDER BY Pay DESC, Name");
    }

    /// <summary>
    /// The inverse. Worth measuring because it is on the path of anything that logs, caches by, or hands back
    /// the query it was given rather than the one it parsed.
    /// </summary>
    [Benchmark(Description = "write: three terms back to text")]
    public string WriteTypical()
    {
        return Workloads.TypicalCondition.ToQuery();
    }
}
