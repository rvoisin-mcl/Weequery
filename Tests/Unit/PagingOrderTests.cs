using Microsoft.EntityFrameworkCore;
using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// Paging without a sort is accepted, and answers arbitrarily. These pin what that produces, since it is the
/// behaviour the remarks on ApplyPagination describe and the reason they are there.
/// <para>
/// The trap is that it looks correct in memory, where Skip and Take follow the sequence's own order, and only goes
/// wrong against a database, where nothing in the SQL asks for an order at all.
/// </para>
/// </summary>
public class PagingOrderTests
{
    private static string StatementFor(TestProvider provider, Sort? sort)
    {
        using var context = TestDatabase.Create(provider);

        var queryString = context.Minions
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplySort(sort)
            .ApplyPagination(pageSize: 2, page: 1)
            .Build()
            .ToQueryString();

        return TestDatabase.StatementOnly(queryString);
    }

    /// <summary>
    /// No sort, no ordering: the row window is taken from a result the database may produce in any order, and may
    /// produce differently for the next page
    /// </summary>
    [Theory]
    [InlineData(TestProvider.Sqlite)]
    [InlineData(TestProvider.PostgreSql)]
    public void PagingWithoutASortAsksForNoOrderAtAll(TestProvider provider)
    {
        var statement = StatementFor(provider, sort: null);

        Assert.Contains("LIMIT", statement);
        Assert.DoesNotContain("ORDER BY", statement);
    }

    /// <summary>
    /// SQL Server's OFFSET/FETCH is only legal after an ORDER BY, so EF Core supplies one that orders by a
    /// constant. It reads like an ordering and is not one, which is the most misleading of the three.
    /// </summary>
    [Fact]
    public void SqlServerPagingWithoutASortOrdersByAConstant()
    {
        var statement = StatementFor(TestProvider.SqlServer, sort: null);

        Assert.Contains("ORDER BY (SELECT 1)", statement);
        Assert.Contains("OFFSET", statement);
    }

    /// <summary>
    /// With a sort there is a real ordering for the window to be a window of
    /// </summary>
    [Theory]
    [MemberData(nameof(TestDatabase.AllProviders), MemberType = typeof(TestDatabase))]
    public void PagingWithASortOrdersByTheSortedColumn(TestProvider provider)
    {
        var statement = StatementFor(provider, new Sort(nameof(Minion.Pay), SortDirection.Ascending));

        Assert.Contains("ORDER BY", statement);
        Assert.Contains("Pay", statement);
        Assert.DoesNotContain("(SELECT 1)", statement);
    }

    /// <summary>
    /// Why the mistake survives a test suite: in memory Skip and Take follow the order the sequence enumerates in,
    /// so an unsorted page is stable and looks like it works. The same code against a database has no such promise.
    /// </summary>
    [Fact]
    public void InMemoryPagingWithoutASortFollowsTheSequenceOrder()
    {
        string[] Page(int page)
        {
            return MinionTestData.Minions()
                .WithWeequery()
                .BindProperties(Minion.Bindings)
                .ApplyPagination(pageSize: 2, page: page)
                .Build()
                .Select(minion => minion.Name)
                .ToArray();
        }

        // The order the test data is declared in, which is the only reason this is predictable
        Assert.Equal(["Alice Fox", "Bob Samuelson"], Page(0));
        Assert.Equal(["Charlie Smith", "David Edgars"], Page(1));
    }
}
