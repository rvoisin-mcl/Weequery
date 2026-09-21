using Microsoft.EntityFrameworkCore;
using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// BuildPaged hands back the page and, alongside it, the query counting what the page is a page of. These pin the
/// two apart: the page carries the filter, the sorts and the window, and the count query carries the filter and
/// nothing else.
/// <para>
/// The mistake it exists to prevent is counting the page, which answers with the size of the page. The mistake it
/// can still be used to make is counting the rows after the window rather than before, which is why the count
/// query is built from the filtered query directly rather than from the page.
/// </para>
/// </summary>
public class PagedQueryTests
{
    /// <summary>
    /// Pay > 0 leaves three of the four, so a page size of two makes two pages of an unevenly divided set, which
    /// is the case where a count and a page length differ
    /// </summary>
    private static PagedQuery<Minion> InMemory(int page, bool paginate = true)
    {
        var inquiry = MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition("Pay > 0")
            .ApplySorts("Pay DESC");

        if (paginate) { inquiry = inquiry.ApplyPagination(pageSize: 2, page: page); }

        return inquiry.BuildPaged();
    }

    private static string[] Names(IQueryable<Minion> query)
    {
        return query.ToList().Select(minion => minion.Name.Split(' ')[0]).ToArray();
    }

    // ---------- what each half holds ----------

    [Fact]
    public void PageIsTheRequestedWindowInTheRequestedOrder()
    {
        Assert.Equal(["Charlie", "Alice"], Names(InMemory(page: 0).Page));
        Assert.Equal(["David"], Names(InMemory(page: 1).Page));
    }

    /// <summary>
    /// The whole point: three matched, whichever page of two was asked for
    /// </summary>
    [Fact]
    public void MatchesCountsEverythingTheFilterMatchedRatherThanThePage()
    {
        Assert.Equal(3, InMemory(page: 0).Matches.Count());
        Assert.Equal(3, InMemory(page: 1).Matches.Count());

        // and the page it came with is the smaller number, which is the mistake being guarded against
        Assert.Equal(2, InMemory(page: 0).Page.Count());
    }

    /// <summary>
    /// It carries the filter, so the row Pay > 0 excluded is not in it either
    /// </summary>
    [Fact]
    public void MatchesCarriesTheFilterButNotTheWindow()
    {
        Assert.Equal(["Alice", "Charlie", "David"], Names(InMemory(page: 0).Matches).Order().ToArray());
    }

    [Fact]
    public void WithoutPaginationThePageIsEverythingAndAgreesWithTheCount()
    {
        var paged = InMemory(page: 0, paginate: false);

        Assert.Equal(["Charlie", "Alice", "David"], Names(paged.Page));
        Assert.Equal(3, paged.Matches.Count());
    }

    [Fact]
    public void Deconstructs()
    {
        var (page, matches) = InMemory(page: 0);

        Assert.Equal(["Charlie", "Alice"], Names(page));
        Assert.Equal(3, matches.Count());
    }

    // ---------- the statements the two produce ----------

    private sealed record Statements(string Page, string Matches);

    private static Statements StatementsFor(TestProvider provider)
    {
        using var context = TestDatabase.Create(provider);

        var (page, matches) = context.Minions
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition("Pay > 0")
            .ApplySorts("Pay DESC")
            .ApplyPagination(pageSize: 2, page: 1)
            .BuildPaged();

        return new Statements(TestDatabase.StatementOnly(page.ToQueryString()), TestDatabase.StatementOnly(matches.ToQueryString()));
    }

    /// <summary>
    /// Nothing depends on the order of a count, and a database asked for one would sort rows it is about to
    /// discard. Nothing depends on the window either, and applying it would count the page.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestDatabase.AllProviders), MemberType = typeof(TestDatabase))]
    public void TheCountQueryAsksForNoOrderingAndNoWindow(TestProvider provider)
    {
        Assert.SkipUnless(TestDatabase.ProviderSqlIsPinned, TestDatabase.ProviderSqlUnpinned);

        var (page, matches) = StatementsFor(provider);

        Assert.Contains("WHERE", matches);
        Assert.DoesNotContain("ORDER BY", matches);
        Assert.DoesNotContain("LIMIT", matches);
        Assert.DoesNotContain("OFFSET", matches);

        // while the page it came with asked for both
        Assert.Contains("ORDER BY", page);
    }

    /// <summary>
    /// Both are built from one filtered query, so there is no route by which the count can be over a different
    /// set of rows than the page was taken from
    /// </summary>
    [Theory]
    [MemberData(nameof(TestDatabase.AllProviders), MemberType = typeof(TestDatabase))]
    public void BothHalvesCarryTheSameFilter(TestProvider provider)
    {
        Assert.SkipUnless(TestDatabase.ProviderSqlIsPinned, TestDatabase.ProviderSqlUnpinned);

        var (page, matches) = StatementsFor(provider);

        string Where(string statement)
        {
            var start = statement.IndexOf("WHERE", StringComparison.Ordinal);
            var end = statement.IndexOf("ORDER BY", StringComparison.Ordinal);

            return (end > start) ? statement[start..end].Trim() : statement[start..].Trim();
        }

        Assert.Equal(Where(matches), Where(page));
    }

    // ---------- refusals happen where Build's do ----------

    /// <summary>
    /// Building both halves resolves the sorts, so a sort that Build would refuse is refused here too, and at the
    /// same point rather than when one of the two queries is enumerated
    /// </summary>
    [Fact]
    public void ASortBuildWouldRefuseIsRefusedHereToo()
    {
        var inquiry = MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplySorts("Nonexistent");

        Assert.Throws<WeequeryException>(() => inquiry.BuildPaged());
    }

    [Fact]
    public void ThePageIsTheSameQueryBuildWouldHaveReturned()
    {
        string Built(bool paged)
        {
            var inquiry = MinionTestData.Minions()
                .WithWeequery()
                .BindProperties(Minion.Bindings)
                .ApplyCondition("Pay > 0")
                .ApplySorts("Pay DESC")
                .ApplyPagination(pageSize: 2, page: 0);

            return (paged ? inquiry.BuildPaged().Page : inquiry.Build()).Expression.ToString();
        }

        Assert.Equal(Built(paged: false), Built(paged: true));
    }
}
