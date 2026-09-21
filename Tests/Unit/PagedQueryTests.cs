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
    private static PagedQuery<Minion> InMemory(int page, bool paginate = true, string condition = "Pay > 0")
    {
        var inquiry = MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition(condition)
            .ApplySorts("Pay DESC");

        if (paginate) { inquiry = inquiry.ApplyPagination(pageSize: 2, page: page); }

        return inquiry.BuildPaged();
    }

    private static string[] Names(IQueryable<Minion> query)
    {
        return query.ToList().Select(minion => minion.Name.Split(' ')[0]).ToArray();
    }

    /// <summary>
    /// The one ending that is right, and the one everything below reaches for. See PagedQuery.Total for the
    /// others and for what they answer instead.
    /// </summary>
    private static int Total(PagedQuery<Minion> paged)
    {
        return paged.Total.FirstOrDefault();
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
    public void TheTotalIsEverythingTheFilterMatchedRatherThanThePage()
    {
        Assert.Equal(3, Total(InMemory(page: 0)));
        Assert.Equal(3, Total(InMemory(page: 1)));

        // and the page it came with is the smaller number, which is the mistake being guarded against
        Assert.Equal(2, InMemory(page: 0).Page.Count());
    }

    /// <summary>
    /// It carries the filter, so the row Pay > 0 excluded is not counted either
    /// </summary>
    [Fact]
    public void TheTotalCarriesTheFilterButNotTheWindow()
    {
        Assert.Equal(4, MinionTestData.Minions().Count());

        Assert.Equal(3, Total(InMemory(page: 0)));
        Assert.Equal(3, Total(InMemory(page: 1, paginate: false)));
    }

    [Fact]
    public void WithoutPaginationThePageIsEverythingAndAgreesWithTheCount()
    {
        var paged = InMemory(page: 0, paginate: false);

        Assert.Equal(["Charlie", "Alice", "David"], Names(paged.Page));
        Assert.Equal(3, Total(paged));
    }

    [Fact]
    public void Deconstructs()
    {
        var (page, total) = InMemory(page: 0);

        Assert.Equal(["Charlie", "Alice"], Names(page));
        Assert.Equal(3, total.FirstOrDefault());
    }

    // ---------- the total is a query of one number, and that is not free ----------

    /// <summary>
    /// The trap the shape carries, pinned so that it stays a documented one. Total is a query holding a single
    /// row, so counting it answers about the count query rather than about the count: it compiles, it looks like
    /// the obvious thing to write, and it is wrong quietly.
    /// </summary>
    [Fact]
    public void CountingTheTotalAnswersAboutTheQueryRatherThanAboutTheCount()
    {
        var (_, total) = InMemory(page: 0);

        Assert.Equal(1, total.Count());          // one row came back
        Assert.Equal(3, total.FirstOrDefault()); // holding the three that matched
    }

    /// <summary>
    /// A grouping over no rows is no group rather than a group of none, so a filter nothing matched leaves a
    /// query with no row in it at all. FirstOrDefault answers with the zero that was wanted; Single has nothing
    /// to give back.
    /// </summary>
    [Fact]
    public void ATotalOfNothingReadsBackAsZero()
    {
        var (_, total) = InMemory(page: 0, condition: "Pay > 999999");

        Assert.Empty(total.ToList());
        Assert.Equal(0, total.FirstOrDefault());
        Assert.Throws<InvalidOperationException>(() => total.Single());
    }

    // ---------- the statements the two produce ----------

    private sealed record Statements(string Page, string Total);

    private static Statements StatementsFor(TestProvider provider)
    {
        using var context = TestDatabase.Create(provider);

        var (page, total) = context.Minions
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition("Pay > 0")
            .ApplySorts("Pay DESC")
            .ApplyPagination(pageSize: 2, page: 1)
            .BuildPaged();

        return new Statements(TestDatabase.StatementOnly(page.ToQueryString()), TestDatabase.StatementOnly(total.ToQueryString()));
    }

    /// <summary>
    /// The database is asked for a number, so no column of the entity is anywhere in the statement and nothing
    /// of a row crosses the wire. Npgsql writes the aggregate as count(*)::int, which is why this reads the
    /// shape rather than a fixed string.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestDatabase.AllProviders), MemberType = typeof(TestDatabase))]
    public void TheCountQueryReadsANumberRatherThanARow(TestProvider provider)
    {
        Assert.SkipUnless(TestDatabase.ProviderSqlIsPinned, TestDatabase.ProviderSqlUnpinned);

        var (page, total) = StatementsFor(provider);

        Assert.Matches(@"(?i)^SELECT\s+count\(\*\)", total);

        Assert.DoesNotContain("\"Name\"", total, StringComparison.Ordinal);
        Assert.DoesNotContain("[Name]", total, StringComparison.Ordinal);

        // while the page it came with reads the entity, which is what a page is for
        Assert.Contains("Name", page, StringComparison.Ordinal);
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

        var (page, total) = StatementsFor(provider);

        Assert.Contains("WHERE", total, StringComparison.Ordinal);
        Assert.DoesNotContain("ORDER BY", total, StringComparison.Ordinal);
        Assert.DoesNotContain("LIMIT", total, StringComparison.Ordinal);
        Assert.DoesNotContain("OFFSET", total, StringComparison.Ordinal);

        // while the page it came with asked for both
        Assert.Contains("ORDER BY", page, StringComparison.Ordinal);
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

        var (page, total) = StatementsFor(provider);

        // The count nests the filtered rows in a subquery, so its WHERE ends at the bracket closing that
        // subquery as well as at the clauses that can follow one
        static string Where(string statement)
        {
            string[] ends = [")", "ORDER BY", "GROUP BY", "LIMIT", "OFFSET"];

            var lines = statement.Split('\n');
            var start = Array.FindIndex(lines, line => line.StartsWith("WHERE", StringComparison.Ordinal));

            return string.Join(
                "\n",
                lines
                    .Skip(start)
                    .TakeWhile((line, index) => (index == 0) || (!ends.Any(end => line.StartsWith(end, StringComparison.Ordinal)))));
        }

        Assert.Equal(Where(total), Where(page));
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
