using BenchmarkDotNet.Attributes;
using System.Linq.Expressions;

namespace Weequery.Benchmarks;

/// <summary>
/// Turning a condition into an expression tree, which is the cost that is actually Weequery's.
/// </summary>
/// <remarks>
/// <para>
/// No database, no provider, no rows. Once this hands back a predicate the query is an ordinary
/// <c>IQueryable.Where</c> and everything after it costs what it would have cost had a person written the lambda
/// which is the claim <c>ComparisonOperatorsEmitTheSameStatementAsHandWrittenLinq</c> makes deterministically
/// over in the test suite. So this figure, plus the parse beside it, is the whole of what the library adds to a
/// request.
/// </para>
/// <para>
/// <see cref="Inquiry{T}.BuildExpression"/> resolves its bindings through the process wide cache, so what is
/// measured here is a cache hit and the tree walk, which is what a running application pays. What it costs
/// cold is in <see cref="BindingBenchmarks"/>.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class ExpressionBenchmarks
{
    [Benchmark(Baseline = true, Description = "build: one comparison")]
    public Expression<Func<Henchman, bool>> BuildSimple()
    {
        return Inquiry<Henchman>.BuildExpression(Workloads.Bindings, Workloads.SimpleCondition);
    }

    [Benchmark(Description = "build: three terms")]
    public Expression<Func<Henchman, bool>> BuildTypical()
    {
        return Inquiry<Henchman>.BuildExpression(Workloads.Bindings, Workloads.TypicalCondition);
    }

    [Benchmark(Description = "build: nested, list, range")]
    public Expression<Func<Henchman, bool>> BuildComplex()
    {
        return Inquiry<Henchman>.BuildExpression(Workloads.Bindings, Workloads.ComplexCondition);
    }

    /// <summary>
    /// Compiling as well, which is what filtering in memory costs and what a caller caching a delegate pays once.
    /// Expect this to dwarf everything else here: the compiler is not ours and it is the reason
    /// <see cref="Inquiry{T}.BuildDelegate"/> is worth calling once rather than per request.
    /// </summary>
    [Benchmark(Description = "build and compile: three terms")]
    public Func<Henchman, bool> CompileTypical()
    {
        return Inquiry<Henchman>.BuildDelegate(Workloads.Bindings, Workloads.TypicalCondition);
    }

    // ---------- the compile on its own, ours against the C# compiler's, for the same predicate ----------

    /// <summary>
    /// The tree the C# compiler writes for the same predicate, and the tree this library writes for it. Built
    /// once here so the two rows below are timing <c>Compile</c> and nothing else.
    /// </summary>
    private Expression<Func<Henchman, bool>> HandWrittenTree = henchman => true;

    /// <inheritdoc cref="HandWrittenTree"/>
    private Expression<Func<Henchman, bool>> WeequeryTree = henchman => true;

    /// <summary>
    /// Prepare the two trees.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        HandWrittenTree = henchman => henchman.IsActive
            && (henchman.Pay > 10000m)
            && (henchman.Alias != null)
            && henchman.Alias.StartsWith("Al", StringComparison.Ordinal);

        WeequeryTree = Inquiry<Henchman>.BuildExpression(Workloads.Bindings, Workloads.TypicalCondition);
    }

    /// <summary>
    /// What <c>Expression.Compile</c> costs on a tree written by the C# compiler.
    /// </summary>
    /// <remarks>
    /// The reference point for the row below it. Compilation is not linear in anything simple, and a tree with
    /// more nodes in it costs more to compile, so a library that emits a larger tree for the same predicate
    /// pays for that every time something compiles it, which on an <c>AsQueryable</c> is every enumeration.
    /// </remarks>
    [Benchmark(Description = "compile only: the C# compiler's tree")]
    public Func<Henchman, bool> CompileHandWrittenTree()
    {
        return HandWrittenTree.Compile();
    }

    /// <summary>
    /// The same, on the tree this library built for the same predicate. Whatever separates this from the row
    /// above is the price of the extra nodes: the null guards, and any conversion put in beside a value.
    /// </summary>
    [Benchmark(Description = "compile only: Weequery's tree")]
    public Func<Henchman, bool> CompileWeequeryTree()
    {
        return WeequeryTree.Compile();
    }
}
