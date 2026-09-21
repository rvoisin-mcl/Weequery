using Microsoft.EntityFrameworkCore;
using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// What a quantifier becomes when a provider has to translate it.
/// <para>
/// The case it matters most for. Indexing a collection asks a provider for one element, which is awkward in SQL
/// and translates differently everywhere; quantifying over one asks the question SQL was built to answer, so it
/// becomes an EXISTS subquery and the elements never leave the database.
/// </para>
/// <para>
/// Shares the model and the context with <see cref="CollectionIndexProviderTests"/>, and like it needs no server:
/// EF produces SQL from the provider and the model alone.
/// </para>
/// </summary>
public class QuantifierProviderTests
{
    private static string Sql(TestProvider provider, string query)
    {
        using var context = CollectionIndexProviderTests.Context(provider);

        var inquiry = context.Shipments
            .WithWeequery()
            .BindProperty(shipment => shipment.Id)
            .BindCollection(shipment => shipment.Legs, "Legs", inner => inner
                .BindProperty(leg => leg.Miles));

        return TestDatabase.StatementOnly(inquiry.ApplyCondition(query).Build().ToQueryString())
            .Replace("\r", " ")
            .Replace("\n", " ");
    }

    /// <summary>
    /// The whole point: the elements are counted where they live. Nothing is materialized, and the value still
    /// goes as a parameter like every other value.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestDatabase.AllProviders), MemberType = typeof(TestDatabase))]
    public void AnyBecomesExists(TestProvider provider)
    {
        var sql = Sql(provider, "Legs Any (Miles > 100)");

        Assert.Contains("EXISTS", sql);
        Assert.DoesNotContain("NOT EXISTS", sql);
        Assert.Contains("@Value", sql);
        Assert.DoesNotContain("100", sql);
    }

    /// <summary>
    /// Both of the ones that are true of nothing come out as NOT EXISTS, which is also why they need no guard:
    /// a subquery that finds no rows is false, so its negation is true, whether the collection is empty or absent.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestDatabase.AllProviders), MemberType = typeof(TestDatabase))]
    public void AllAndNoneBecomeNotExists(TestProvider provider)
    {
        Assert.Contains("NOT EXISTS", Sql(provider, "Legs All (Miles > 100)"));
        Assert.Contains("NOT EXISTS", Sql(provider, "Legs None (Miles > 100)"));
    }

    /// <summary>The condition inside travels into the subquery whole, however many tests it holds</summary>
    [Theory]
    [MemberData(nameof(TestDatabase.AllProviders), MemberType = typeof(TestDatabase))]
    public void TheInnerConditionTranslatesToo(TestProvider provider)
    {
        var sql = Sql(provider, "Legs Any (Miles > 100 AND Miles < 500)");

        Assert.Contains("EXISTS", sql);
        Assert.Contains("@Value", sql);
        Assert.Contains("@Value1", sql);
    }

    /// <summary>And it composes with the rest of the query rather than splitting it in two</summary>
    [Theory]
    [MemberData(nameof(TestDatabase.AllProviders), MemberType = typeof(TestDatabase))]
    public void AQuantifierCombinesWithAnOrdinaryTest(TestProvider provider)
    {
        var sql = Sql(provider, "Id > 1 AND Legs Any (Miles > 100)");

        var where = sql[sql.IndexOf("WHERE", StringComparison.Ordinal)..];

        // One WHERE holding both halves, the comparison and the subquery, rather than two queries
        Assert.Contains("EXISTS", where);
        Assert.Contains("@Value", where);
        Assert.Contains("@Value1", where);
    }
}
