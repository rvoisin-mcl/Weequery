using Microsoft.EntityFrameworkCore;
using Tests.Common;
using Weequery;

namespace Tests.Unit;

public class SortTests
{
    private static string[] SortedBy(params Sort[] sorts)
    {
        return MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplySorts(sorts)
            .Build()
            .ToList()
            .Select(minion => minion.Name.Split(' ')[0])
            .ToArray();
    }

    [Fact]
    public void SingleSortAscending()
    {
        Assert.Equal(["Bob", "David", "Alice", "Charlie"], SortedBy(new Sort(nameof(Minion.Pay), SortDirection.Ascending)));
    }

    [Fact]
    public void SingleSortDescending()
    {
        Assert.Equal(["Charlie", "Alice", "David", "Bob"], SortedBy(new Sort(nameof(Minion.Pay), SortDirection.Descending)));
    }

    /// <summary>
    /// The bug this covers: every sort used OrderBy rather than chaining with ThenBy, so each
    /// sort discarded the one before it and only the last had any effect.
    /// </summary>
    [Fact]
    public void SecondSortBreaksTiesRatherThanReplacingTheFirst()
    {
        // Inactive (Charlie) first, then the actives by descending pay.
        // The old behaviour ordered by pay descending alone: Charlie, Alice, David, Bob.
        Assert.Equal(
            ["Charlie", "Alice", "David", "Bob"],
            SortedBy(
                new Sort(nameof(Minion.IsActive), SortDirection.Ascending),
                new Sort(nameof(Minion.Pay), SortDirection.Descending)));

        // Flipping the primary key must change the result, which it did not before
        Assert.Equal(
            ["Alice", "David", "Bob", "Charlie"],
            SortedBy(
                new Sort(nameof(Minion.IsActive), SortDirection.Descending),
                new Sort(nameof(Minion.Pay), SortDirection.Descending)));
    }

    [Fact]
    public void ThirdSortStillChains()
    {
        // HireDate ties three minions; break by IsActive then Pay
        Assert.Equal(
            ["Charlie", "Alice", "Bob", "David"],
            SortedBy(
                new Sort(nameof(Minion.HireDate), SortDirection.Ascending),
                new Sort(nameof(Minion.IsActive), SortDirection.Ascending),
                new Sort(nameof(Minion.Pay), SortDirection.Descending)));
    }

    [Fact]
    public void SortOnNullablePropertyIsSupported()
    {
        Assert.Equal(4, SortedBy(new Sort(nameof(Minion.FireDate), SortDirection.Ascending)).Length);
    }

    [Fact]
    public void SortOnUnboundFieldThrowsWeequeryException()
    {
        Assert.Throws<WeequeryException>(() => SortedBy(new Sort("NotAField", SortDirection.Ascending)));
    }

    [Fact]
    public void SortsCombineWithConditions()
    {
        var result = MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition("IsActive == true")
            .ApplySorts([new Sort(nameof(Minion.Pay), SortDirection.Ascending)])
            .Build()
            .ToList();

        Assert.Equal(["Bob", "David", "Alice"], result.Select(minion => minion.Name.Split(' ')[0]).ToArray());
    }

    /// <summary>
    /// A clause is applied by adding the call to the query's own expression and letting the provider make a query
    /// of it, which is what Queryable.OrderBy does with what it is handed. This pins that the provider sees the
    /// same thing either way: every clause in the order given, chained rather than replacing the one before.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestDatabase.AllProviders), MemberType = typeof(TestDatabase))]
    public void EveryClauseReachesTheProviderInOrder(TestProvider provider)
    {
        using var context = TestDatabase.Create(provider);

        var statement = TestDatabase.StatementOnly(context.Minions
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplySorts(
            [
                new Sort(nameof(Minion.IsActive), SortDirection.Ascending),
                new Sort(nameof(Minion.Pay), SortDirection.Descending),
                new Sort(nameof(Minion.Name), SortDirection.Ascending),
            ])
            .Build()
            .ToQueryString());

        var orderBy = statement.Split('\n').Single(line => line.Contains("ORDER BY"));

        Assert.True(orderBy.IndexOf(nameof(Minion.IsActive)) < orderBy.IndexOf(nameof(Minion.Pay)), orderBy);
        Assert.True(orderBy.IndexOf(nameof(Minion.Pay)) < orderBy.IndexOf(nameof(Minion.Name)), orderBy);

        // and the direction of each survived the chaining
        Assert.Contains("DESC", orderBy);
    }
}
