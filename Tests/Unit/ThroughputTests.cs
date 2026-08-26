using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// Compares a Weequery built query against hand written equivalents, to show what Weequery costs over writing the
/// query yourself.
/// <para>
/// The comparison has two halves. The SQL shape tests are deterministic and always run: they show that for the
/// comparison operators Weequery emits the same statement a hand written LINQ query does, which is the real
/// argument that execution cannot be slower. The timed tests measure it, and are opt in through
/// WEEQUERY_TEST_THROUGHPUT because they seed tens of thousands of rows and would otherwise dominate the suite.
/// </para>
/// <para>
/// Timings are reported rather than asserted. A wall clock threshold in a test suite fails on a loaded build agent
/// for reasons that have nothing to do with the library. What is asserted is that every approach returns the same
/// rows, so the numbers are being compared on equal work.
/// </para>
/// </summary>
public class ThroughputTests
{
    private const string EnableVariable = "WEEQUERY_TEST_THROUGHPUT";
    private const int RowCount = 20_000;
    private const int Iterations = 25;

    private readonly ITestOutputHelper Output;

    public ThroughputTests(ITestOutputHelper output)
    {
        Output = output;
    }

    private static bool Enabled
    {
        get { return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnableVariable)); }
    }

    // ---------- deterministic: what SQL does Weequery emit next to a hand written query ----------

    /// <summary>
    /// EF names parameters after whatever they came from, so Weequery's "@Value" and a lambda's "@threshold" are
    /// the same statement with a different label. Normalize the names so the shapes can be compared.
    /// </summary>
    private static string NormalizedStatement(string queryString)
    {
        return Regex.Replace(TestDatabase.StatementOnly(queryString), "@[A-Za-z0-9_]+", "@p");
    }

    /// <summary>
    /// For the comparison operators Weequery produces exactly the statement a hand written Where produces. Since
    /// the database is handed identical SQL, it cannot execute more slowly; the only cost Weequery can add is the
    /// expression building measured below.
    /// </summary>
    [Fact]
    public void ComparisonOperatorsEmitTheSameStatementAsHandWrittenLinq()
    {
        using var context = TestDatabase.Create(TestProvider.Sqlite);

        var threshold = 10000m;

        var cases = new (string Query, IQueryable<Minion> HandWritten)[]
        {
            ("Pay > 10000", context.Minions.Where(minion => minion.Pay > threshold)),
            ("Pay >= 10000", context.Minions.Where(minion => minion.Pay >= threshold)),
            ("Pay < 10000", context.Minions.Where(minion => minion.Pay < threshold)),
            ("Pay == 10000", context.Minions.Where(minion => minion.Pay == threshold)),
            ("Pay != 10000", context.Minions.Where(minion => minion.Pay != threshold)),
        };

        foreach (var (query, handWritten) in cases)
        {
            var weequery = context.Minions.WithWeequery().BindProperties(Minion.Bindings).ApplyCondition(query).Build();

            Assert.Equal(NormalizedStatement(handWritten.ToQueryString()), NormalizedStatement(weequery.ToQueryString()));
        }
    }

    /// <summary>
    /// Weequery guards a string column against null before calling StartsWith. On a column the model knows cannot
    /// be null, EF's own nullability analysis removes that guard again, so the statement is identical to a hand
    /// written one and the guard is free.
    /// </summary>
    [Fact]
    public void TheNullGuardCostsNothingOnANonNullableColumn()
    {
        using var context = TestDatabase.Create(TestProvider.Sqlite);

        var prefix = "Al";

        // Minion.Name is non-nullable
        var weequery = NormalizedStatement(context.Minions
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition("Name StartsWith 'Al'")
            .Build()
            .ToQueryString());

        var handWritten = NormalizedStatement(context.Minions.Where(minion => minion.Name.StartsWith(prefix)).ToQueryString());

        Assert.DoesNotContain("IS NOT NULL", weequery);
        Assert.Equal(handWritten, weequery);
    }

    /// <summary>
    /// On a genuinely nullable column the guard survives, so Weequery asks the database for one test more than a
    /// bare hand written StartsWith. That is the trade being made: the hand written version would throw on a null
    /// when the same predicate runs in memory, where Weequery returns false.
    /// </summary>
    [Fact]
    public void TheNullGuardIsTheOneExtraTestOnANullableColumn()
    {
        using var context = TestDatabase.Create(TestProvider.Sqlite);

        var prefix = "Al";

        // Minion.Alias is nullable
        var weequery = NormalizedStatement(context.Minions
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition("Alias StartsWith 'Al'")
            .Build()
            .ToQueryString());

        var handWritten = NormalizedStatement(context.Minions.Where(minion => minion.Alias!.StartsWith(prefix)).ToQueryString());

        Assert.Contains("IS NOT NULL", weequery);
        Assert.DoesNotContain("IS NOT NULL", handWritten);

        // and the guard is the only difference: take it out and the two statements are the same
        var withoutGuard = Regex.Replace(weequery, "\"[^\"]+\"\\.\"Alias\" IS NOT NULL AND ", string.Empty);

        Assert.Equal(handWritten, withoutGuard);
    }

    /// <summary>
    /// Why the timed comparisons below filter on a bool rather than on Pay.
    /// <para>
    /// SQLite has no decimal type, so EF stores a decimal column as TEXT and compares it through its own
    /// ef_compare helper. The obvious hand written equivalent, "WHERE Pay &gt; 10000", is a lexicographic string
    /// comparison, so '9999' and '500' both sort above '10000' and come back as matches. It looks faster than the
    /// EF query only because it is answering a different question, and ef_compare is not available to a plain
    /// command, so there is no quick way to hand write the correct one.
    /// </para>
    /// <para>
    /// Worth having as a test in its own right: it is a case where going through Weequery is more correct than the
    /// SQL a person would reach for.
    /// </para>
    /// </summary>
    [Fact]
    public void NaiveHandWrittenSqlGetsDecimalComparisonsWrongOnSqlite()
    {
        Assert.SkipUnless(Enabled, $"{EnableVariable} is not set, so the seeded comparison tests are skipped");

        var context = Seeded(TestProvider.Sqlite);
        try
        {
            var table = context.Model.FindEntityType(typeof(Minion))!.GetTableName();

            var throughWeequery = context.Minions
                .WithWeequery()
                .BindProperties(Minion.Bindings)
                .ApplyCondition("Pay > 10000")
                .Build()
                .Count();

            var handWrittenLinq = context.Minions.Count(minion => minion.Pay > 10000m);

            var naiveSql = TestDatabase
                .HandWrittenQuery<int>(context, $"SELECT COUNT(*) AS \"Value\" FROM \"{table}\" WHERE \"Pay\" > {{0}}", 10000m)
                .Single();

            // Weequery agrees with EF, because it hands EF the same comparison
            Assert.Equal(handWrittenLinq, throughWeequery);

            // and the naive SQL does not, because it is comparing text
            Assert.NotEqual(handWrittenLinq, naiveSql);

            Output.WriteLine($"Pay > 10000 over {RowCount:N0} rows");
            Output.WriteLine($"  Weequery / EF (ef_compare)     {throughWeequery} rows   <- correct");
            Output.WriteLine($"  naive hand written SQL         {naiveSql} rows   <- lexicographic TEXT compare");
        }
        finally
        {
            TestDatabase.Drop(context);
        }
    }

    // ---------- measured: how long each approach takes over the same rows ----------

    private sealed record Measurement(string Approach, TimeSpan Elapsed, int Rows)
    {
        public double PerQueryMs { get { return Elapsed.TotalMilliseconds / Iterations; } }
    }

    private void Report(string scenario, List<Measurement> measurements)
    {
        var baseline = measurements.First(measurement => measurement.Approach.StartsWith("EF LINQ")).PerQueryMs;

        Output.WriteLine($"{scenario}   ({RowCount:N0} rows seeded, {Iterations} iterations)");
        Output.WriteLine($"  {"approach",-34} {"ms/query",10} {"vs EF LINQ",12}   rows");

        foreach (var measurement in measurements)
        {
            Output.WriteLine($"  {measurement.Approach,-34} {measurement.PerQueryMs,10:F3} {measurement.PerQueryMs / baseline,11:F2}x   {measurement.Rows}");
        }

        Output.WriteLine(string.Empty);
    }

    private static Measurement Measure(string approach, Func<int> run)
    {
        // Warm up first: EF pays for model building and query compilation once, and timing that would say nothing
        // about steady state throughput
        var rows = run();
        run();

        var stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < Iterations; i++) { run(); }
        stopwatch.Stop();

        return new Measurement(approach, stopwatch.Elapsed, rows);
    }

    private static DBContext Seeded(TestProvider provider)
    {
        var context = TestDatabase.Create(provider);
        context.Database.EnsureCreated();

        var random = new Random(20260822);
        string[] firstNames = ["Alice", "Bob", "Charlie", "David", "Edith", "Fred", "Greta", "Hank"];
        string[] lastNames = ["Fox", "Samuelson", "Smith", "Edgars", "Crane", "Wall", "Yoder", "Stone"];

        List<Minion> minions = new(RowCount);
        for (int i = 0; i < RowCount; i++)
        {
            minions.Add(new Minion
            {
                MinionID = Guid.NewGuid(),
                Name = $"{firstNames[random.Next(firstNames.Length)]} {lastNames[random.Next(lastNames.Length)]}",
                Alias = (i % 5 == 0) ? null : $"Alias{i}",
                IsActive = (i % 3) != 0,
                Pay = random.Next(0, 20000),
                PreferredCurrency = "US$",
                BirthDate = new DateTime(1980, 1, 1).AddDays(i % 10000),
                HireDate = new DateTime(2010, 1, 1).AddDays(i % 5000),
                FireDate = (i % 4 == 0) ? new DateTime(2024, 12, 25) : null,
                CauseForDeparture = (i % 4 == 0) ? "Eaten by shark" : null,
                Classification = (i % 7 == 0) ? Classification.Irreplacable : Classification.Expendible,
            });
        }

        context.Minions.AddRange(minions);
        context.SaveChanges();

        return context;
    }

    /// <summary>
    /// The main comparison: full row materialization, which is what a real caller does.
    /// </summary>
    [Fact]
    public void MaterializingRowsCostsTheSameThroughWeequeryAsThroughHandWrittenLinq()
    {
        Assert.SkipUnless(Enabled, $"{EnableVariable} is not set, so the timed throughput tests are skipped");

        // A bool column, deliberately: SQLite stores bool as INTEGER, so the hand written SQL below is genuinely
        // the same query. See NaiveHandWrittenSqlGetsDecimalComparisonsWrongOnSqlite for why a decimal column
        // would not be.
        var active = true;
        var context = Seeded(TestProvider.Sqlite);
        try
        {
            var table = context.Model.FindEntityType(typeof(Minion))!.GetTableName();

            // Built fresh every iteration, which is how the fluent API is normally used
            int ViaWeequery() => context.Minions
                .WithWeequery()
                .BindProperties(Minion.Bindings)
                .ApplyCondition("IsActive == true")
                .Build()
                .ToList()
                .Count;

            // Predicate built once and reused, for callers that cache it
            var predicate = Weequery.Inquiry<Minion>.BuildExpression(Minion.Bindings, ConditionFunctions.ParseQuery("IsActive == true")!);
            int WeequeryPrebuilt() => context.Minions.Where(predicate).ToList().Count;

            int EfLinq() => context.Minions.Where(minion => minion.IsActive).ToList().Count;

            int RawSelect() => TestDatabase
                .HandWrittenEntities<Minion>(context, $"SELECT * FROM \"{table}\" WHERE \"IsActive\" = {{0}}", active)
                .ToList()
                .Count;

            var measurements = new List<Measurement>
            {
                Measure("EF LINQ (hand written Where)", EfLinq),
                Measure("Weequery (built per query)", ViaWeequery),
                Measure("Weequery (predicate reused)", WeequeryPrebuilt),
                Measure("Direct SELECT via FromSqlRaw", RawSelect),
            };

            Report("Materialize rows matching IsActive", measurements);

            // The numbers are only comparable if every approach did the same work
            Assert.All(measurements, measurement => Assert.Equal(measurements[0].Rows, measurement.Rows));
            Assert.True(measurements[0].Rows > 0, "the filter matched nothing, so nothing was measured");
        }
        finally
        {
            TestDatabase.Drop(context);
        }
    }

    /// <summary>
    /// A scalar count, which strips materialization out and leaves the SQL and the round trip. Includes a plain
    /// ADO.NET command as the floor, with no EF in the path at all.
    /// </summary>
    [Fact]
    public void CountingRowsCostsTheSameThroughWeequeryAsThroughRawAdo()
    {
        Assert.SkipUnless(Enabled, $"{EnableVariable} is not set, so the timed throughput tests are skipped");

        var active = true;
        var context = Seeded(TestProvider.Sqlite);
        try
        {
            var table = context.Model.FindEntityType(typeof(Minion))!.GetTableName();

            int ViaWeequery() => context.Minions
                .WithWeequery()
                .BindProperties(Minion.Bindings)
                .ApplyCondition("IsActive == true")
                .Build()
                .Count();

            int EfLinq() => context.Minions.Count(minion => minion.IsActive);

            int RawSelect() => TestDatabase
                .HandWrittenQuery<int>(context, $"SELECT COUNT(*) AS \"Value\" FROM \"{table}\" WHERE \"IsActive\" = {{0}}", active)
                .Single();

            int Ado()
            {
                var connection = context.Database.GetDbConnection();
                if (connection.State != System.Data.ConnectionState.Open) { connection.Open(); }

                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT COUNT(*) FROM \"{table}\" WHERE \"IsActive\" = $active";

                var parameter = command.CreateParameter();
                parameter.ParameterName = "$active";
                parameter.Value = active;
                command.Parameters.Add(parameter);

                return Convert.ToInt32(command.ExecuteScalar());
            }

            var measurements = new List<Measurement>
            {
                Measure("EF LINQ (hand written Count)", EfLinq),
                Measure("Weequery (built per query)", ViaWeequery),
                Measure("Direct SELECT via SqlQueryRaw", RawSelect),
                Measure("Direct SELECT via ADO.NET", Ado),
            };

            Report("Count rows matching IsActive", measurements);

            Assert.All(measurements, measurement => Assert.Equal(measurements[0].Rows, measurement.Rows));
            Assert.True(measurements[0].Rows > 0, "the filter matched nothing, so nothing was measured");
        }
        finally
        {
            TestDatabase.Drop(context);
        }
    }

    /// <summary>
    /// Isolates the cost that is actually Weequery's: turning a condition into an expression tree, with no
    /// database involved.
    /// </summary>
    [Fact]
    public void BuildingThePredicateIsTheOnlyCostWeequeryAdds()
    {
        Assert.SkipUnless(Enabled, $"{EnableVariable} is not set, so the timed throughput tests are skipped");

        const int builds = 20_000;
        var condition = ConditionFunctions.ParseQuery("Pay > 10000")!;

        // warm up
        for (int i = 0; i < 100; i++) { Weequery.Inquiry<Minion>.BuildExpression(Minion.Bindings, condition); }

        var parse = Stopwatch.StartNew();
        for (int i = 0; i < builds; i++) { ConditionFunctions.ParseQuery("Pay > 10000"); }
        parse.Stop();

        var build = Stopwatch.StartNew();
        for (int i = 0; i < builds; i++) { Weequery.Inquiry<Minion>.BuildExpression(Minion.Bindings, condition); }
        build.Stop();

        Output.WriteLine($"Per operation, over {builds:N0} iterations, no database involved");
        Output.WriteLine($"  parse query string   {parse.Elapsed.TotalMilliseconds * 1000 / builds,8:F2} us");
        Output.WriteLine($"  build expression     {build.Elapsed.TotalMilliseconds * 1000 / builds,8:F2} us");
        Output.WriteLine($"  total per query      {(parse.Elapsed + build.Elapsed).TotalMilliseconds * 1000 / builds,8:F2} us");
    }
}
