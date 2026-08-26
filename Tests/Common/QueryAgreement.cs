using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text;
using Weequery;

namespace Tests.Common;

/// <summary>
/// One condition, asked of a database and of the same rows in memory, and held to the same answer.
/// </summary>
/// <remarks>
/// <para>
/// The comparison is the point of these tests, so a failure has to say which side was wrong and why, not just that
/// the two differed. A disagreement here has three plausible causes and they are told apart by different evidence:
/// the condition means different things to the two evaluators (the SQL and the row sets show it), the database was
/// not holding what the test thought (the table dump shows it), or something transient (the second read of each
/// side shows it). All of it is gathered on failure and none of it on the way past, so the passing run costs
/// nothing.
/// </para>
/// <para>
/// Written because <see cref="Tests.Unit.StringOrderingTests.InMemoryAgreesWithTheDatabase"/> failed twice with no
/// message worth reading, and passed on every attempt to reproduce it. The next occurrence should be diagnosable
/// from its output alone.
/// </para>
/// </remarks>
internal static class QueryAgreement
{
    /// <summary>
    /// Assert that a query selects the same rows through the provider as it does in memory.
    /// </summary>
    /// <param name="query">in the query language</param>
    /// <param name="provider">defaults to the one that runs without a server</param>
    /// <exception cref="Xunit.Sdk.XunitException">the two disagree, with everything gathered about why</exception>
    public static void AssertSameRows(string query, TestProvider provider = TestProvider.Sqlite)
    {
        var context = TestDatabase.CreateSeeded(provider);

        try
        {
            var built = Build(context.Minions, query);

            var fromDatabase = Names(built);
            var inMemory = Names(Build(MinionTestData.Minions(), query));

            if (fromDatabase.SequenceEqual(inMemory)) { return; }

            Assert.Fail(Report(context, provider, query, built, fromDatabase, inMemory));
        }
        finally
        {
            TestDatabase.Drop(context);
        }
    }

    private static IQueryable<Minion> Build(IQueryable<Minion> rows, string query)
    {
        return rows
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition(query)
            .Build();
    }

    private static string[] Names(IQueryable<Minion> rows)
    {
        return [.. rows.Select(minion => minion.Name).ToList().Order()];
    }

    /// <summary>
    /// Everything worth knowing about a disagreement, gathered once it has already happened.
    /// </summary>
    private static string Report(DBContext context, TestProvider provider, string query, IQueryable<Minion> built, string[] fromDatabase, string[] inMemory)
    {
        var report = new StringBuilder();

        report.AppendLine($"'{query}' selected different rows in the database and in memory.");
        report.AppendLine();
        report.AppendLine($"  database  [{string.Join(", ", fromDatabase)}]  ({fromDatabase.Length} rows)");
        report.AppendLine($"  in memory [{string.Join(", ", inMemory)}]  ({inMemory.Length} rows)");
        report.AppendLine($"  database only [{string.Join(", ", fromDatabase.Except(inMemory))}]");
        report.AppendLine($"  memory only   [{string.Join(", ", inMemory.Except(fromDatabase))}]");
        report.AppendLine();

        // Whether it says the same thing twice. A different answer on the second read is a transient, and rules out
        // the condition simply meaning two things.
        Describe(report, "second read, database", () => $"[{string.Join(", ", Names(built))}]");
        Describe(report, "second read, in memory", () => $"[{string.Join(", ", Names(Build(MinionTestData.Minions(), query)))}]");
        report.AppendLine();

        // What the provider was actually asked, which is where a difference in meaning shows up. The condition
        // first, since that is the part being questioned, then the whole statement in case it is not.
        Describe(report, "where", () => Condition(built));
        Describe(report, "sql", () => TestDatabase.StatementOnly(built.ToQueryString()).ReplaceLineEndings(" "));
        Describe(report, "parameters", () => TestDatabase.ParameterCount(built.ToQueryString()).ToString());
        report.AppendLine();

        // What the database was holding, so a seed that did not land is not mistaken for a condition that
        // disagrees. Read raw, rather than through the model that built the query being questioned.
        Describe(report, "rows in the database", () => string.Join(" | ", RawRows(context)));
        Describe(report, "rows in memory", () => string.Join(" | ", from minion in MinionTestData.Minions() select $"{minion.Name} alias={minion.Alias ?? "<null>"}"));
        report.AppendLine();

        report.AppendLine($"  provider {provider}, culture '{CultureInfo.CurrentCulture.Name}', invariant globalization {InvariantGlobalization}");

        return report.ToString();
    }

    /// <summary>
    /// The condition the provider was given, which is the part of the statement in question. The whole statement
    /// is reported as well, so nothing is lost if the difference turns out to be somewhere else in it.
    /// </summary>
    private static string Condition(IQueryable<Minion> built)
    {
        var statement = TestDatabase.StatementOnly(built.ToQueryString()).ReplaceLineEndings(" ");
        var where = statement.IndexOf("WHERE", StringComparison.Ordinal);

        return (where < 0) ? "<no condition in the statement>" : statement[where..];
    }

    /// <summary>
    /// Add one line of evidence, saying so rather than throwing if it cannot be gathered: a diagnostic that fails
    /// must not replace the failure being diagnosed.
    /// </summary>
    private static void Describe(StringBuilder report, string label, Func<string> evidence)
    {
        try
        {
            report.AppendLine($"  {label,-22} {evidence()}");
        }
        catch (Exception ex)
        {
            report.AppendLine($"  {label,-22} <unavailable: {ex.GetType().Name}: {ex.Message.ReplaceLineEndings(" ")}>");
        }
    }

    /// <summary>
    /// The rows as the database holds them, read without going through the query under question
    /// </summary>
    private static List<string> RawRows(DBContext context)
    {
        var table = context.Model.FindEntityType(typeof(Minion))!.GetTableName();

        return TestDatabase
            .HandWrittenQuery<string>(context, $"SELECT \"Name\" || ' alias=' || COALESCE(\"Alias\", '<null>') AS \"Value\" FROM \"{table}\" ORDER BY \"Name\"")
            .ToList();
    }

    /// <summary>
    /// Whether the runtime is comparing strings without ICU, which changes what a culture sensitive comparison
    /// means and so is worth knowing when two of them disagree
    /// </summary>
    private static bool InvariantGlobalization
    {
        get
        {
            // The same letter composed two ways: one code point, or an e with a combining accent. A culture
            // sensitive comparison calls those equal and an ordinal one does not, which is the difference to detect.
            return string.Compare("é", "e?", StringComparison.CurrentCulture) != 0;
        }
    }
}
