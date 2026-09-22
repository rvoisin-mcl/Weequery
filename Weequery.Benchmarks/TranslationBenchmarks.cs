using BenchmarkDotNet.Attributes;
using System.Text.Json.Nodes;
using Weequery.Elasticsearch;
using Weequery.OData;

namespace Weequery.Benchmarks;

/// <summary>
/// Writing a condition out for a target that is not an <see cref="IQueryable{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Both of these share their tree walk with <see cref="ConditionTranslator{TResult, TScope}"/> and differ only in
/// what they write, so the two columns being close together is the expected result and the interesting one would
/// be them drifting apart. What separates them is the shape they build: a <c>$filter</c> is a string, and an
/// Elasticsearch query is a <see cref="JsonObject"/> graph, which allocates per node.
/// </para>
/// <para>
/// This is entirely CPU and allocation with no I/O anywhere in it, which makes it the most stable thing here and
/// the part of the published table that can be trusted between machines.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class TranslationBenchmarks
{
    [Benchmark(Baseline = true, Description = "OData: three terms")]
    public string ODataTypical()
    {
        return ODataFilter.Write(Workloads.TypicalCondition, Workloads.ODataFields);
    }

    [Benchmark(Description = "OData: nested, list, range")]
    public string ODataComplex()
    {
        return ODataFilter.Write(Workloads.ComplexCondition, Workloads.ODataFields);
    }

    [Benchmark(Description = "OData: quantifier")]
    public string ODataQuantified()
    {
        return ODataFilter.Write(Workloads.QuantifiedCondition, Workloads.ODataFields);
    }

    [Benchmark(Description = "Elasticsearch: three terms")]
    public JsonObject ElasticTypical()
    {
        return ElasticQuery.Build(Workloads.TypicalCondition, Workloads.ElasticFields);
    }

    [Benchmark(Description = "Elasticsearch: nested, list, range")]
    public JsonObject ElasticComplex()
    {
        return ElasticQuery.Build(Workloads.ComplexCondition, Workloads.ElasticFields);
    }

    [Benchmark(Description = "Elasticsearch: quantifier")]
    public JsonObject ElasticQuantified()
    {
        return ElasticQuery.Build(Workloads.QuantifiedCondition, Workloads.ElasticFields);
    }

    /// <summary>
    /// The JSON as well, since a query object nobody serialized has not been sent anywhere.
    /// </summary>
    [Benchmark(Description = "Elasticsearch: three terms, to JSON")]
    public string ElasticTypicalJson()
    {
        return ElasticQuery.ToJson(Workloads.TypicalCondition, Workloads.ElasticFields);
    }
}
