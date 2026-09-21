using System.Text.Json;
using BenchmarkDotNet.Attributes;

namespace Weequery.Benchmarks;

/// <summary>
/// Getting a condition across the wire, in both of the shapes it travels in.
/// </summary>
/// <remarks>
/// <para>
/// A condition built by a client is packed, serialized, posted, deserialized and unpacked, and every one of those
/// is on the request path of anything using the object graph form. The alternative is the query language, which
/// is a string on the wire and pays a parse instead. Which of the two is cheaper is a fair question to ask of a
/// library that offers both, and it should be answered with a measurement rather than an opinion.
/// </para>
/// <para>
/// The serialization is <c>System.Text.Json</c> with no options, since that is what an ASP.NET endpoint does with
/// a <c>PackedCondition</c> parameter and there is no point measuring a configuration nobody has.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class TransportBenchmarks
{
    private string PackedJson = "";

    [GlobalSetup]
    public void Setup()
    {
        PackedJson = JsonSerializer.Serialize(Workloads.TypicalCondition.Pack());
    }

    /// <summary>The sending half of the object graph form</summary>
    [Benchmark(Baseline = true, Description = "graph: pack and serialize")]
    public string PackAndSerialize()
    {
        return JsonSerializer.Serialize(Workloads.TypicalCondition.Pack());
    }

    /// <summary>The receiving half, which is the one that runs on the server per request</summary>
    [Benchmark(Description = "graph: deserialize and unpack")]
    public ICondition DeserializeAndUnpack()
    {
        return JsonSerializer.Deserialize<PackedCondition>(PackedJson)!.Unpack();
    }

    /// <summary>The same trip as a string, for comparison: writing it</summary>
    [Benchmark(Description = "text: write to a query string")]
    public string WriteQuery()
    {
        return Workloads.TypicalCondition.ToQuery();
    }

    /// <summary>and reading it back, which is the server side of the string form</summary>
    [Benchmark(Description = "text: parse a query string")]
    public ICondition? ReadQuery()
    {
        return ConditionFunctions.ParseQuery(Workloads.Typical);
    }
}
