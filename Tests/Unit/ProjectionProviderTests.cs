using Microsoft.EntityFrameworkCore;
using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// What a projection becomes when a provider has to translate it.
/// <para>
/// The point of projecting at all: three columns of a wide table read as three columns, rather than a whole row
/// fetched and then thrown away. So what these check is the SELECT list.
/// </para>
/// <para>
/// Shares the model and the context with <see cref="CollectionIndexProviderTests"/>, and like it needs no server.
/// </para>
/// </summary>
public class ProjectionProviderTests
{
    private static string Sql(TestProvider provider, string? projection, string? condition = null)
    {
        using var context = CollectionIndexProviderTests.Context(provider);

        var inquiry = context.Shipments
            .WithWeequery()
            .BindProperty(shipment => shipment.Id)
            .BindProperty(shipment => shipment.Tags)
            .BindProperty(shipment => shipment.Counts)
            .ApplyProjection(projection);

        if (condition is not null) { inquiry = inquiry.ApplyCondition(condition); }

        return TestDatabase.StatementOnly(inquiry.BuildProjected().ToQueryString())
            .Replace("\r", " ")
            .Replace("\n", " ");
    }

    private static string SelectList(string sql)
    {
        var from = sql.IndexOf("FROM", StringComparison.Ordinal);

        Assert.True(from > 0, $"expected a FROM, got: {sql}");

        return sql[..from];
    }

    [Theory]
    [MemberData(nameof(TestDatabase.AllProviders), MemberType = typeof(TestDatabase))]
    public void OnlyTheProjectedColumnsAreRead(TestProvider provider)
    {
        var select = SelectList(Sql(provider, "Id"));

        Assert.Contains("Id", select);
        Assert.DoesNotContain("Tags", select);
        Assert.DoesNotContain("Counts", select);
    }

    [Theory]
    [MemberData(nameof(TestDatabase.AllProviders), MemberType = typeof(TestDatabase))]
    public void MoreThanOneReadsMoreThanOne(TestProvider provider)
    {
        var select = SelectList(Sql(provider, "Id, Tags"));

        Assert.Contains("Id", select);
        Assert.Contains("Tags", select);
        Assert.DoesNotContain("Counts", select);
    }

    /// <summary>Filtering and projecting are separate, and a filter on a column not read still translates</summary>
    [Theory]
    [MemberData(nameof(TestDatabase.AllProviders), MemberType = typeof(TestDatabase))]
    public void AConditionOnAnUnprojectedColumnStillApplies(TestProvider provider)
    {
        Assert.SkipUnless(TestDatabase.ProviderSqlIsPinned, TestDatabase.ProviderSqlUnpinned);

        var sql = Sql(provider, "Tags", "Id > 1");

        Assert.DoesNotContain("Id", SelectList(sql));
        Assert.Contains("WHERE", sql);
        Assert.Contains("@Value", sql);
    }

    /// <summary>Nothing asked for is everything allowed, and that is still a named list rather than a star</summary>
    [Theory]
    [MemberData(nameof(TestDatabase.AllProviders), MemberType = typeof(TestDatabase))]
    public void NoProjectionReadsEveryBoundColumn(TestProvider provider)
    {
        var select = SelectList(Sql(provider, null));

        Assert.Contains("Id", select);
        Assert.Contains("Tags", select);
        Assert.Contains("Counts", select);
        Assert.DoesNotContain("*", select);
    }

    /// <summary>
    /// An index is read where the collection lives, exactly as it is in a condition.
    /// </summary>
    /// <remarks>
    /// Read off the whole statement rather than the select list, since SQL Server reaches an element through
    /// OPENJSON and so writes a subquery with a FROM of its own ahead of the outer one. What matters is the same
    /// either way: the indexed collection is read and the column nobody asked for is not.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestDatabase.AllProviders), MemberType = typeof(TestDatabase))]
    public void AnIndexedFieldProjects(TestProvider provider)
    {
        var sql = Sql(provider, "Tags[0]");

        Assert.Contains("Tags", sql);
        Assert.DoesNotContain("Counts", sql);
    }
}
