using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using System.Linq.Expressions;

namespace Weequery.Benchmarks;

/// <summary>
/// Where the per row cost actually goes: the null guard, the expression tree, or neither.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FilteringBenchmarks"/> answers "what does Weequery cost per row" with one number. This takes it
/// apart, because that number has two things in it and only one of them is Weequery's.
/// </para>
/// <para>
/// <b>Three predicates per category, and the middle one is the important one.</b> A C# lambda is an ordinary
/// method the JIT can inline into the loop that calls it. The same predicate built as an expression tree and
/// handed to <c>Compile</c> is a <c>DynamicMethod</c>, which it cannot. That difference belongs to .NET rather
/// than to this library, and without a row measuring it the whole of it would be charged here. So the middle row
/// is the identical predicate, written by the C# compiler, compiled the way Weequery's is: what separates it
/// from the first row is the cost of going through an expression tree at all, and what separates it from the
/// third is the only part this library is responsible for.
/// </para>
/// <para>
/// <b>The categories are the null guard, and which one applies is decided by the property's type rather than by
/// anything the caller wrote.</b> A binding guards when the accessor is a reference type, a nullable value type,
/// or reached through either, so <see cref="Henchman.Pay"/> and <see cref="Henchman.IsActive"/> are not
/// guarded, and <see cref="Henchman.Name"/> is, however non-nullable it is declared. Nullable reference
/// annotations are erased by the time a binding is built, and a string that a deserializer or an outer join put
/// a null into is a null whatever the declaration promised.
/// </para>
/// <para>
/// Grouped by category so each ratio is against its own baseline. Comparing across categories would be comparing
/// different predicates, and a ratio only means something over the same work.
/// </para>
/// </remarks>
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[MemoryDiagnoser]
public class GuardBenchmarks
{
    /// <summary>Enough rows that per row work is what is measured, rather than the call overhead around it</summary>
    private const int RowCount = 10_000;

    private const string ValuesCategory = "1. value types, unguarded";
    private const string NullableCategory = "2. nullable string, all guard";
    private const string TextCategory = "3. non-nullable string, only Weequery guards";

    private List<Henchman> Henchmen = [];

    private Func<Henchman, bool> ValuesTree = henchman => true;
    private Func<Henchman, bool> NullableTree = henchman => true;
    private Func<Henchman, bool> TextTree = henchman => true;

    private Func<Henchman, bool> ValuesWeequery = henchman => true;
    private Func<Henchman, bool> NullableWeequery = henchman => true;
    private Func<Henchman, bool> TextWeequery = henchman => true;

    [GlobalSetup]
    public void Setup()
    {
        Henchmen = Workloads.Rows(RowCount);

        // The C# compiler's own expression tree for each predicate, compiled. Identical logic to the lambdas
        // below, reached the same way Weequery's predicate is reached.
        Expression<Func<Henchman, bool>> values = henchman => henchman.IsActive && (henchman.Pay > 10000m);
        Expression<Func<Henchman, bool>> nullable = henchman => (henchman.Alias != null) && henchman.Alias.StartsWith("Al", StringComparison.Ordinal);
        Expression<Func<Henchman, bool>> text = henchman => henchman.Name.StartsWith("Al", StringComparison.Ordinal);

        ValuesTree = values.Compile();
        NullableTree = nullable.Compile();
        TextTree = text.Compile();

        ValuesWeequery = Compile(Workloads.Unguarded);
        NullableWeequery = Compile(Workloads.GuardedNullable);
        TextWeequery = Compile(Workloads.GuardedText);

        Agree();
    }

    private static Func<Henchman, bool> Compile(string query)
    {
        return Inquiry<Henchman>.BuildDelegate(Workloads.Bindings, ConditionFunctions.ParseQuery(query)!);
    }

    /// <summary>
    /// Every predicate in a category has to match the same rows, or that category's ratios are over unequal work.
    /// </summary>
    /// <remarks>
    /// The third category is the one this exists for. Weequery guards <see cref="Henchman.Name"/> and the two
    /// predicates beside it do not, which is the point of that comparison, but they still have to agree on the
    /// answer, since no Name in the data is null. If that ever stops being true the ratio stops meaning what it
    /// says, and this says so rather than letting it slide.
    /// </remarks>
    /// <exception cref="InvalidOperationException">a category disagrees, or a filter matched nothing</exception>
    private void Agree()
    {
        Same(nameof(Workloads.Unguarded), HandWrittenValues(), TreeValues(), WeequeryValues());
        Same(nameof(Workloads.GuardedNullable), HandWrittenNullable(), TreeNullable(), WeequeryNullable());
        Same(nameof(Workloads.GuardedText), HandWrittenText(), TreeText(), WeequeryText());
    }

    private static void Same(string what, params int[] counts)
    {
        if (counts.Distinct().Count() != 1)
        {
            throw new InvalidOperationException($"{what}: the predicates matched different rows ({string.Join(", ", counts)}), so their timings do not compare");
        }

        if (counts[0] == 0)
        {
            throw new InvalidOperationException($"{what} matched nothing, so nothing is being measured");
        }
    }

    // ---------- a bool and a decimal: no guard, because neither can be null ----------

    /// <summary>The floor: a lambda the C# compiler turned into a method the JIT can inline</summary>
    [BenchmarkCategory(ValuesCategory), Benchmark(Baseline = true, Description = "C# lambda: bool AND decimal")]
    public int HandWrittenValues()
    {
        return Henchmen.Count(henchman => henchman.IsActive && (henchman.Pay > 10000m));
    }

    /// <summary>The same predicate through an expression tree. What separates this from the row above is not ours.</summary>
    [BenchmarkCategory(ValuesCategory), Benchmark(Description = "compiled expression: bool AND decimal")]
    public int TreeValues()
    {
        return Henchmen.Count(ValuesTree);
    }

    /// <summary>Weequery's, which emits the same two tests and no guard</summary>
    [BenchmarkCategory(ValuesCategory), Benchmark(Description = "Weequery: bool AND decimal")]
    public int WeequeryValues()
    {
        return Henchmen.Count(ValuesWeequery);
    }

    // ---------- a nullable string: everything guards, because everything has to ----------

    /// <summary>
    /// The guard is in the hand written predicate too, because without it this throws on the rows with no alias.
    /// So this category measures everything except the guard.
    /// </summary>
    [BenchmarkCategory(NullableCategory), Benchmark(Baseline = true, Description = "C# lambda: nullable string, guarded")]
    public int HandWrittenNullable()
    {
        return Henchmen.Count(henchman => (henchman.Alias != null) && henchman.Alias.StartsWith("Al", StringComparison.Ordinal));
    }

    /// <inheritdoc cref="TreeValues"/>
    [BenchmarkCategory(NullableCategory), Benchmark(Description = "compiled expression: nullable string, guarded")]
    public int TreeNullable()
    {
        return Henchmen.Count(NullableTree);
    }

    /// <inheritdoc cref="HandWrittenNullable"/>
    [BenchmarkCategory(NullableCategory), Benchmark(Description = "Weequery: nullable string, guarded")]
    public int WeequeryNullable()
    {
        return Henchmen.Count(NullableWeequery);
    }

    // ---------- a non-nullable string: Weequery guards, a person would not ----------

    /// <summary>
    /// What someone writes against a property declared string rather than string?: no guard, on the strength of
    /// the declaration.
    /// </summary>
    [BenchmarkCategory(TextCategory), Benchmark(Baseline = true, Description = "C# lambda: non-nullable string, unguarded")]
    public int HandWrittenText()
    {
        return Henchmen.Count(henchman => henchman.Name.StartsWith("Al", StringComparison.Ordinal));
    }

    /// <inheritdoc cref="TreeValues"/>
    [BenchmarkCategory(TextCategory), Benchmark(Description = "compiled expression: non-nullable string, unguarded")]
    public int TreeText()
    {
        return Henchmen.Count(TextTree);
    }

    /// <summary>
    /// The same thing through Weequery, which guards it anyway. Against the row above (same predicate, same
    /// compilation, no guard), this is the guard on its own, with nothing else left in it.
    /// </summary>
    [BenchmarkCategory(TextCategory), Benchmark(Description = "Weequery: non-nullable string, guarded")]
    public int WeequeryText()
    {
        return Henchmen.Count(TextWeequery);
    }
}
