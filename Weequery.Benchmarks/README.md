# Weequery.Benchmarks

*[produces a stopwatch]*

You asked what [Weequery](https://github.com/rvoisin-mcl/Weequery) costs you. It is a fair question, it is very
nearly the only fair question anybody has asked me all week, and I have had it **measured** rather than merely
asserted.

```bash
dotnet run -c Release --project Weequery.Benchmarks
```

Everything runs, and the results land in `BenchmarkDotNet.Artifacts/results/`. The GitHub flavoured markdown in
there is what [BENCHMARKS.md](../BENCHMARKS.md) is made of.

**Nothing here ships.** It is not packable, nothing that ships references it, and it takes no dependency of its
own beyond BenchmarkDotNet. It exists to hold me to account, which I permit, occasionally, under supervision.

## What is measured, and why these things

The performance claim has two halves, and only one of them is a benchmark at all.

**The half that is already proven** is execution. `ComparisonOperatorsEmitTheSameStatementAsHandWrittenLinq`,
over in the test suite, shows that for the comparison operators I emit byte for byte the statement a hand
written `Where` emits. A server handed identical SQL cannot run it more slowly. That is a stronger argument than
any timing I could offer you, because it does not depend in the slightest on the machine it was made on.

**The half left to measure** is everything on this side of the wire: reading the caller's filter, resolving the
allow-list, and building the expression tree. That is what these are for.

| | |
|---|---|
| `ParsingBenchmarks` | reading a condition out of text, and writing one back |
| `ExpressionBenchmarks` | turning a condition into an expression tree, the cost that is genuinely mine |
| `BindingBenchmarks` | declaring the allow-list, three ways, which do not cost the same |
| `PipelineBenchmarks` | a whole request, from text to a query ready to enumerate, nothing executed |
| `FilteringBenchmarks` | actually filtering rows, against a hand written query over the same rows |
| `GuardBenchmarks` | where the per row cost goes: the null guard, the expression tree, or neither |
| `TranslationBenchmarks` | writing the condition out as an Elasticsearch query or an OData `$filter` |
| `TransportBenchmarks` | getting a condition across the wire, as an object graph and as a string |

Every one of them reports allocations as well as time, and for a library that sits in a request path the
allocation column is usually the more interesting of the two. Look at it.

All of them run against the same four conditions, in [Workloads.cs](Workloads.cs), so that a figure from one
table can be read against a figure from another. The parse cost and the build cost of the same filter are two
halves of one number, and they only add up if both halves measured the same string.

| | |
|---|---|
| **Simple** | `Pay > 10000`, the floor |
| **Typical** | three terms and a string operator, which is what a filter bar sends |
| **Complex** | nesting, a list, a range, a null test and a negation, the advanced search |
| **Quantified** | `Assignments Any (...)`, the one shape that reaches into another type |

### The model is deliberately not the test model

`Henchman` is defined here rather than borrowed from the test suite, and that is not an oversight. Half of what
these measure is the *size of the model*, how many bindings get resolved and how far a path walks, so sharing one
would mean that a property somebody added to cover a parsing edge case silently moved every published figure,
and nobody on earth would connect the two. I have enough unexplained results in my life.

## Reading the results honestly

*[leans forward]*

Now pay attention, because this is where people embarrass themselves.

**`FilteringBenchmarks` compares a compiled delegate against a compiled lambda**, which is the right pair for
"what does a row cost", but **the ratio it gives is not all mine, and `GuardBenchmarks` is where I take it
apart.** A delegate from `Expression.Compile` is a `DynamicMethod` that the JIT will not inline into the loop
calling it, where a C# lambda is an ordinary method that it will. That gap belongs to .NET, not to me. Running
the identical predicate three ways, as a lambda, as a compiled expression, and through me, shows most of it
sitting in the compilation rather than in this library. Read that table before you quote this one.

**A compiled predicate is not quite the expression a provider gets.** `BuildDelegate` writes caller values in as
constants, where `BuildExpression` reads them out of a holder so that EF Core makes them query parameters
instead of SQL literals. The same rows are selected either way; the constants are cheaper to compile and
marginally cheaper per row, and there is no parameter worth preserving when nothing is going to translate the
thing. `ExpressionBenchmarks` prices both, and the difference is larger than it looks: it is most of the distance
between a tree I build and one the C# compiler does.

**The three rows using `AsQueryable` are not a fair comparison, and they are in the table to show you why.** LINQ
to Objects compiles the expression tree *every single time it enumerates*. That cost does not move when the row
count moves by a factor of a hundred, and handing it the same expression object twice does not help either,
because it is not per row work. A ratio taken against one of those would say far more about `Expression.Compile`
than about me, and it would **flatter** me, since both sides pay the same compile and the difference between
them vanishes into it. I decline to be flattered. They are worth exactly one thing: they are the argument for
`BuildDelegate` over `Build` when the data is already in memory. Against a real provider none of it applies,
because EF Core caches its query plans.

**In memory, not against a database.** A database benchmark would mostly measure the database, and I have no
interest in publishing a table about somebody else's disk. The execution case is made by the SQL shape tests, as
above.

**One set is measured against other libraries**, and it is fenced off on purpose. `ComparisonBenchmarks` puts me
beside Sieve, Fop and Gridify on the intersection of what all four can express: a filter, a sort and a page.

The hazard it was kept out of the main set for is still there. The cross-library filtering benchmarks in this
space run a few filters over a couple of dozen rows through `AsQueryable` and report *milliseconds*, where the
filtering itself is microseconds and the rest is `Expression.Compile`, which `EnumerableQuery` pays on every
enumeration and which every library present pays equally. That compresses everything except the genuine outliers
into a narrow band around 1.00, which is mostly a ratio of compile costs and tells you nothing whatsoever. So my
table carries a hand written predicate as its baseline, taking no filter string and using no library at all,
which makes the floor visible and turns every other row into a distance from it rather than a ranking among
themselves. Read it that way or do not read it. And take the per row question to the tables that answer it
without a compile standing in the way.

**Absolute numbers belong to the machine that produced them.** BenchmarkDotNet writes the CPU, the OS and the
runtime above every table, and that header is not decoration. It is the only thing that lets a reader tell a real
regression from a different laptop. Do not quote a figure from here without it. I will find out.

## Publishing a run

```bash
dotnet run -c Release --project Weequery.Benchmarks
cp BenchmarkDotNet.Artifacts/results/*-report-github.md <somewhere>
```

Regenerate [BENCHMARKS.md](../BENCHMARKS.md) from a quiet machine. Nothing else running, on mains power, and not
on a laptop that thermally throttles halfway through and hands you a fiction. It takes about fifteen minutes.
Find something to do.

For a quick look while you are changing something, the short job cuts that to about two minutes at the cost of
the confidence intervals:

```bash
dotnet run -c Release --project Weequery.Benchmarks -- --job Short
```

And to run one table rather than all of them:

```bash
dotnet run -c Release --project Weequery.Benchmarks -- --filter *ExpressionBenchmarks*
```

Every other BenchmarkDotNet argument works too, `--list` included. They are passed straight through, untouched.

## Regression detection, and what it can honestly catch

The full JSON export exists so that a build can compare one run against the last. Two things are worth knowing
before you go wiring that up in a fit of enthusiasm:

**A GitHub hosted runner is a shared vCPU.** Run to run drift of tens of percent is entirely normal and has
nothing whatsoever to do with the code. A tight alert threshold on one of those trains everybody to ignore the
alert, which is strictly worse than having no alert at all. Set it loose (200%, to catch a cliff rather than a
wobble) and read the trend across many runs for anything smaller.

**Benchmarks do not belong on every pull request.** Fifteen minutes per push, for numbers that noisy, buys you
very nearly nothing. A nightly schedule and a manual trigger is the right shape, and it keeps the
`contents: write` permission a results branch needs well away from the build workflow, which has
`contents: read` and is going to keep it.
