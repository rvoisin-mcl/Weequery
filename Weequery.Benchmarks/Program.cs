using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Running;

namespace Weequery.Benchmarks;

/// <summary>
/// Runs the benchmarks and writes the results out in the two forms anything downstream reads.
/// </summary>
/// <remarks>
/// <para>
/// The GitHub flavoured markdown comes from the default configuration and needs no help: it is the table that
/// goes into BENCHMARKS.md, with the machine, the runtime and the CPU written above it. That header is not
/// decoration. An absolute figure without the machine it came from is not a measurement, and it is what lets a
/// reader tell a real regression from a different laptop.
/// </para>
/// <para>
/// The full JSON is the one addition, and it is what a regression check reads: the default writes a brief form
/// that leaves out the per iteration measurements any comparison over time needs.
/// </para>
/// </remarks>
public static class Program
{
    /// <summary>
    /// Entry point.
    /// </summary>
    /// <param name="args">
    /// passed through to BenchmarkDotNet, so --filter, --list and the rest work as they do anywhere else. With
    /// none, everything runs: a published set of numbers is the whole set or it is not comparable with the last
    /// one, and the interactive menu cannot be what a build server gets
    /// </param>
    public static void Main(string[] args)
    {
        var config = DefaultConfig.Instance.AddExporter(JsonExporter.Full);

        BenchmarkSwitcher
            .FromAssembly(typeof(Program).Assembly)
            .Run((args.Length > 0) ? args : ["--filter", "*"], config);
    }
}
