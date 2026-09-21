using Microsoft.EntityFrameworkCore;
using Tests.Common;
using Weequery;

namespace Tests.Unit;

public class Shipment
{
    public int Id { get; set; }

    /// <summary>A primitive collection, which EF stores as a JSON array in one column</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>The same thing as an array</summary>
    public int[] Counts { get; set; } = [];

    /// <summary>A navigation collection, which is rows in another table</summary>
    public List<Leg> Legs { get; set; } = [];
}

public class Leg
{
    public int Id { get; set; }
    public int Miles { get; set; }
}

public class ShipmentContext(DbContextOptions<ShipmentContext> options) : DbContext(options)
{
    public DbSet<Shipment> Shipments { get; set; }
    public DbSet<Leg> Legs { get; set; }
}

/// <summary>A dictionary on an entity, which EF has no mapping for. Kept apart so it poisons only its own model.</summary>
public class Ledger
{
    public int Id { get; set; }
    public Dictionary<string, int> Tallies { get; set; } = [];
}

public class LedgerContext(DbContextOptions<LedgerContext> options) : DbContext(options)
{
    public DbSet<Ledger> Ledgers { get; set; }
}

/// <summary>
/// What indexing a collection becomes when a provider has to translate it.
/// <para>
/// Its own model and its own context, rather than the shared one: this needs a primitive collection and a
/// navigation collection side by side, and adding either to Minion would change the schema every other provider
/// test runs against.
/// </para>
/// <para>
/// Nothing here needs a server. EF produces SQL from the provider and the model alone, which is what these read,
/// so all three run everywhere.
/// </para>
/// </summary>
public class CollectionIndexProviderTests
{
    internal static ShipmentContext Context(TestProvider provider)
    {
        var builder = new DbContextOptionsBuilder<ShipmentContext>();

        switch (provider)
        {
            case TestProvider.PostgreSql: builder.UseNpgsql("Host=localhost;Database=weequery_shape"); break;
            case TestProvider.SqlServer: builder.UseSqlServer("Server=(localdb)\\None;Database=weequery_shape"); break;
            default: builder.UseSqlite("Data Source=weequery_shape.db"); break;
        }

        return new ShipmentContext(builder.Options);
    }

    private static string Where(TestProvider provider, string query, string? path = null, string? key = null)
    {
        using var context = Context(provider);

        var inquiry = context.Shipments
            .WithWeequery()
            .BindProperty(shipment => shipment.Tags)
            .BindProperty(shipment => shipment.Counts)
            .BindProperty(shipment => shipment.Legs);

        if (path is not null) { inquiry = inquiry.BindProperty(path, key!); }

        var sql = TestDatabase.StatementOnly(inquiry.ApplyCondition(query).Build().ToQueryString())
            .Replace("\r", " ")
            .Replace("\n", " ");

        var where = sql.IndexOf("WHERE", StringComparison.Ordinal);

        Assert.True(where >= 0, $"expected a WHERE clause, got: {sql}");

        return sql[where..];
    }

    // ---------- a primitive collection, which is the case this works best for ----------

    /// <summary>
    /// Both halves of the guard survive the trip: the presence test becomes the provider's own length function,
    /// and the element read becomes its own JSON or array access. The value still goes as a parameter.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestDatabase.AllProviders), MemberType = typeof(TestDatabase))]
    public void AListIndexTranslates(TestProvider provider)
    {
        var where = Where(provider, "Tags[0] = 'urgent'");

        Assert.Contains("@Value", where);
        Assert.DoesNotContain("urgent", where);   // parameterized, as every other value is
    }

    [Theory]
    [MemberData(nameof(TestDatabase.AllProviders), MemberType = typeof(TestDatabase))]
    public void AnArrayIndexTranslates(TestProvider provider)
    {
        Assert.Contains("@Value", Where(provider, "Counts[1] > 2"));
    }

    /// <summary>
    /// The point of the guard, in SQL: past the end is asked as a length, so no row is read at an index it does
    /// not have
    /// </summary>
    [Theory]
    [InlineData(TestProvider.Sqlite, "json_array_length")]
    [InlineData(TestProvider.PostgreSql, "cardinality")]
    [InlineData(TestProvider.SqlServer, "OPENJSON")]
    public void ThePresenceTestBecomesTheProvidersOwnLength(TestProvider provider, string expected)
    {
        Assert.Contains(expected, Where(provider, "Tags[0] = 'urgent'"));
    }

    /// <summary>
    /// Postgres arrays start at one, and EF moves the index rather than leaving it to be wrong by one
    /// </summary>
    [Fact]
    public void PostgresIndexesFromOneAndEfAccountsForIt()
    {
        Assert.Contains("[1]", Where(TestProvider.PostgreSql, "Tags[0] = 'urgent'"));
        Assert.Contains("[2]", Where(TestProvider.PostgreSql, "Counts[1] > 2"));
    }

    /// <summary>
    /// IsNull on an element is the absence of one, which is the length test on its own
    /// </summary>
    [Theory]
    [MemberData(nameof(TestDatabase.AllProviders), MemberType = typeof(TestDatabase))]
    public void IsNullOnAnElementTranslatesToTheLengthAlone(TestProvider provider)
    {
        var where = Where(provider, "Tags[9] IsNull");

        Assert.DoesNotContain("@Value", where);
    }

    // ---------- a navigation collection ----------

    /// <summary>
    /// Rows in another table rather than a column, so the guard becomes an EXISTS: there is no element at 0
    /// exactly when the collection has no rows
    /// </summary>
    [Theory]
    [MemberData(nameof(TestDatabase.AllProviders), MemberType = typeof(TestDatabase))]
    public void ANavigationCollectionTranslatesItsPresenceTest(TestProvider provider)
    {
        Assert.Contains("EXISTS", Where(provider, "Legs[0] IsNull"));
    }

    /// <summary>
    /// And reading through one translates as well, as a subquery taking a single row.
    /// <para>
    /// <b>Which row is not defined.</b> The subquery has no ORDER BY, so "element 0" of a navigation collection is
    /// whichever row the database hands back first, and that may differ between runs and between providers. It is
    /// a real answer to a question nobody can quite ask; index a primitive collection, whose order is the order it
    /// was stored in, or bind what you actually mean.
    /// </para>
    /// </summary>
    [Fact]
    public void ReadingThroughANavigationCollectionTranslatesButHasNoDefinedOrder()
    {
        var where = Where(TestProvider.Sqlite, "FirstLeg > 5", "Legs[0].Miles", "FirstLeg");

        Assert.Contains("SELECT", where);
        Assert.Contains("OFFSET 0", where);

        // the caveat, stated as a fact about the SQL: nothing says which row this is
        Assert.DoesNotContain("ORDER BY", where);
    }

    // ---------- what a provider cannot do at all ----------

    /// <summary>
    /// A dictionary has no mapping, so it is not that indexing one fails to translate: the model will not build
    /// with one on it, and the failure arrives before any query does. Index a dictionary in memory, where it
    /// works, and not against a database.
    /// </summary>
    [Fact]
    public void ADictionaryIsNotSomethingEFCanMapAtAll()
    {
        var builder = new DbContextOptionsBuilder<LedgerContext>();
        builder.UseSqlite("Data Source=weequery_shape.db");

        using var context = new LedgerContext(builder.Options);

        // Not a translation failure and not a Weequery one: EF cannot decide what the property even is
        var error = Assert.Throws<InvalidOperationException>(() => context.Ledgers.Where(ledger => ledger.Id > 0).ToQueryString());

        Assert.Contains("Tallies", error.Message);
    }
}
