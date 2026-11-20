# Weequery.Benchmarks

Measures what [Weequery](https://github.com/rvoisin-mcl/Weequery) costs, with BenchmarkDotNet.

```bash
dotnet run -c Release --project Weequery.Benchmarks
```

This runs every benchmark and writes the results to `BenchmarkDotNet.Artifacts/results/`. The GitHub flavoured
markdown there is the source of [BENCHMARKS.md](../BENCHMARKS.md).

**Nothing here ships.** The project is not packable and nothing that ships references it. Its dependencies are
BenchmarkDotNet and the libraries the comparison benchmarks measure against: Fop, Gridify, Sieve and
System.Linq.Dynamic.Core.

## What is measured

The performance claim has two halves, and only one of them needs a benchmark.

**Execution is already proven.** `ComparisonOperatorsEmitTheSameStatementAsHandWrittenLinq`, in the test suite,
shows that for the comparison operators Weequery emits byte for byte the statement a hand-written `Where` emits. A
server given identical SQL cannot run it more slowly, and unlike a timing, that argument does not depend on the
machine.

**Everything on the client side of the wire is measured here:** reading the caller's filter, resolving the
allow-list, and building the expression tree.

| Benchmark | Measures |
|---|---|
| `ParsingBenchmarks` | Reading a condition out of text, and writing one back |
| `ExpressionBenchmarks` | Turning a condition into an expression tree, the cost that is Weequery's own |
| `BindingBenchmarks` | Declaring the allow-list, three ways, which do not cost the same |
| `PipelineBenchmarks` | A whole request, from text to a query ready to enumerate, nothing executed |
| `FilteringBenchmarks` | Filtering rows, against a hand-written query over the same rows |
| `GuardBenchmarks` | Where the per-row cost goes: the null guard, the expression tree, or neither |
| `TranslationBenchmarks` | Writing the condition out as an Elasticsearch query or an OData `$filter` |
| `TransportBenchmarks` | Getting a condition across the wire, as an object graph and as a string |

Every benchmark reports allocations as well as time. For a library in a request path the allocation column is
usually the more significant of the two.

All benchmarks use the same four conditions, defined in [Workloads.cs](Workloads.cs), so a figure in one table can
be read against a figure in another. The parse cost and the build cost of a filter are two halves of one number,
and only add up if both measured the same string.

| Workload | Condition |
|---|---|
| **Simple** | `Pay > 10000`, the floor |
| **Typical** | Three terms and a string operator, what a filter bar sends |
| **Complex** | Nesting, a list, a range, a null test and a negation, an advanced search |
| **Quantified** | `Assignments Any (...)`, the one shape that reaches into another type |

### A separate model

`Henchman` is defined here rather than borrowed from the test suite. Part of what these benchmarks measure is the
*size of the model*: how many bindings are resolved and how far a path walks. With a shared model, a property added
to cover a parsing edge case would silently move every published figure.

## Interpreting the results

**`FilteringBenchmarks` compares a compiled delegate against a compiled lambda**, the right pair for "what does a
row cost", but **not all of the ratio is Weequery's; `GuardBenchmarks` breaks it down.** A delegate from
`Expression.Compile` is a `DynamicMethod`, which the JIT does not inline into the calling loop; a C# lambda is an
ordinary method, which it does. That gap belongs to .NET. Running the identical predicate three ways, as a lambda,
as a compiled expression, and through Weequery, shows most of it in the compilation rather than in the library.
Read that table before quoting this one.

**A compiled predicate is not quite the expression a provider receives.** `BuildDelegate` writes caller values in
as constants; `BuildExpression` reads them from a holder so that EF Core turns them into query parameters rather
than SQL literals. Both select the same rows. Constants are cheaper to compile and marginally cheaper per row, and
there is no parameter worth preserving when nothing translates the expression. `ExpressionBenchmarks` prices both,
and the difference accounts for most of the distance between a tree Weequery builds and one the C# compiler builds.

**The three rows using `AsQueryable` are not a fair comparison, and are included to show why.** LINQ to Objects
compiles the expression tree *on every enumeration*. That cost does not change when the row count changes by a
factor of a hundred, and passing the same expression object twice does not avoid it, because it is not per-row
work. A ratio against one of those rows measures `Expression.Compile` more than Weequery, and **flatters** Weequery,
since both sides pay the same compile and the difference between them disappears into it. Their one use is as the
argument for `BuildDelegate` over `Build` when the data is already in memory. None of it applies against a real
provider, because EF Core caches its query plans.

**Benchmarks run in memory, not against a database.** A database benchmark would mostly measure the database. The
execution case is made by the SQL shape tests described above.

### Comparison with other libraries

`ComparisonBenchmarks` places Weequery beside Sieve, Fop and Gridify on what all four can express: a filter, a sort
and a page. It is kept apart from the main set deliberately.

Cross-library filtering benchmarks commonly run a few filters over a couple of dozen rows through `AsQueryable` and
report *milliseconds*. The filtering itself takes microseconds; the rest is `Expression.Compile`, which
`EnumerableQuery` pays on every enumeration and every library pays equally. That compresses all but the genuine
outliers into a narrow band around 1.00, which is mostly a ratio of compile costs. This table therefore uses a
hand-written predicate as its baseline, taking no filter string and using no library, so the floor is visible and
every other row reads as a distance from it rather than a ranking. For the per-row question, use the tables that
answer it without a compile in the way.

> [!IMPORTANT]
> **Absolute numbers belong to the machine that produced them.** BenchmarkDotNet writes the CPU, OS and runtime
> above every table, and that header is the only way to tell a real regression from a different machine. Do not
> quote a figure without it.

## Publishing a run

```bash
dotnet run -c Release --project Weequery.Benchmarks
cp BenchmarkDotNet.Artifacts/results/*-report-github.md <somewhere>
```

Regenerate [BENCHMARKS.md](../BENCHMARKS.md) on a quiet machine: nothing else running, on mains power, and not on a
laptop that thermally throttles partway through. A full run takes about fifteen minutes.

| Option | Effect |
|---|---|
| `-- --job Short` | About two minutes, at the cost of the confidence intervals; for a quick check during changes |
| `-- --filter *ExpressionBenchmarks*` | Runs one table rather than all of them |

```bash
dotnet run -c Release --project Weequery.Benchmarks -- --job Short
```

```bash
dotnet run -c Release --project Weequery.Benchmarks -- --filter *ExpressionBenchmarks*
```

All other BenchmarkDotNet arguments, `--list` included, are passed through unchanged.

## Regression detection

The full JSON export exists so that a build can compare one run against the previous one. Two constraints apply:

- **A GitHub hosted runner is a shared vCPU.** Run-to-run drift of tens of percent is normal and unrelated to the
  code. A tight alert threshold teaches everyone to ignore the alert, which is worse than no alert. Set it loose
  (200%, to catch a cliff rather than a wobble) and read the trend across many runs for smaller changes.
- **Benchmarks do not belong on every pull request.** Fifteen minutes per push, for numbers that noisy, buys almost
  nothing. Use a nightly schedule and a manual trigger. This also keeps the `contents: write` permission a results
  branch needs away from the build workflow, which has `contents: read` and keeps it.
