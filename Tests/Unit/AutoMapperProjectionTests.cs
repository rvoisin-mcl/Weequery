using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Tests.Common;
using Weequery;
using Weequery.AutoMapper;

namespace Tests.Unit;

/// <summary>What a caller reads a minion back as, which is three of its columns under different names</summary>
public class MinionSummary
{
    public string Name { get; set; } = "";
    public string? Alias { get; set; }
    public decimal Salary { get; set; }
}

/// <summary>
/// Reading an Inquiry's rows back as a DTO through AutoMapper.
/// <para>
/// Weequery decides which rows and AutoMapper decides what a row looks like, so most of what these check is that
/// the two halves stay in their own lane: the filter and the sort are still the entity's, the shape is still the
/// DTO's, and the count is still over rows rather than over what is read off them.
/// </para>
/// </summary>
public class AutoMapperProjectionTests
{
    /// <remarks>
    /// The one argument shape, which is what AutoMapper 14 takes. 15 added an ILoggerFactory alongside it and
    /// dropped this, so moving the package's floor means changing this line, see the package's own README.
    /// </remarks>
    private static IConfigurationProvider Configuration()
    {
        return new MapperConfiguration(config => config
            .CreateMap<Minion, MinionSummary>()
            .ForMember(summary => summary.Salary, to => to.MapFrom(minion => minion.Pay)));
    }

    private static Inquiry<Minion> Bound()
    {
        return MinionTestData.Minions()
            .WithWeequery()
            .BindProperty(minion => minion.Name)
            .BindProperty(minion => minion.Alias)
            .BindProperty(minion => minion.Pay)
            .BindProperty(minion => minion.IsActive);
    }

    // ---------- the shape is the DTO's ----------

    [Fact]
    public void ARowComesBackAsTheDto()
    {
        var rows = Bound().ApplyCondition("Name = 'Alice Fox'").ProjectTo<Minion, MinionSummary>(Configuration()).ToList();

        var row = Assert.Single(rows);

        Assert.Equal("Alice Fox", row.Name);
        Assert.Equal("Ghost", row.Alias);

        // The map renamed it, which is the DTO's business and not Weequery's
        Assert.Equal(12000m, row.Salary);
    }

    [Fact]
    public void AMapperWorksWhereTheConfigurationWould()
    {
        var mapper = new Mapper(Configuration());

        Assert.Equal(4, Bound().ProjectTo<Minion, MinionSummary>(mapper).Count());
    }

    // ---------- the rows are Weequery's ----------

    [Fact]
    public void TheConditionStillApplies()
    {
        var names = Bound().ApplyCondition("Pay > 10000").ProjectTo<Minion, MinionSummary>(Configuration()).ToList().Select(row => row.Name);

        Assert.Equal(["Alice Fox", "Charlie Smith"], names.Order());
    }

    /// <summary>Sorted on the entity, before the projection, which is what lets a sort name a field the DTO lacks</summary>
    [Fact]
    public void TheSortStillAppliesAndIsOverTheEntity()
    {
        var names = Bound()
            .ApplySorts([new Sort("IsActive", SortDirection.Ascending), new Sort("Name", SortDirection.Descending)])
            .ProjectTo<Minion, MinionSummary>(Configuration())
            .ToList()
            .Select(row => row.Name);

        // Charlie is the only inactive one, and IsActive is nowhere on the DTO
        Assert.Equal(["Charlie Smith", "David Edgars", "Bob Samuelson", "Alice Fox"], names);
    }

    [Fact]
    public void TheWindowStillApplies()
    {
        var rows = Bound()
            .ApplySorts([new Sort("Name", SortDirection.Ascending)])
            .ApplyPagination(pageSize: 2, page: 1)
            .ProjectTo<Minion, MinionSummary>(Configuration())
            .ToList();

        Assert.Equal(["Charlie Smith", "David Edgars"], rows.Select(row => row.Name));
    }

    /// <summary>A field nobody bound is refused exactly as it is anywhere else</summary>
    [Fact]
    public void TheAllowListStillDecides()
    {
        Assert.Throws<WeequeryException>(() => Bound().ApplyCondition("Morale > 1").ProjectTo<Minion, MinionSummary>(Configuration()).ToList());
    }

    // ---------- paged ----------

    [Fact]
    public void ThePageIsProjectedAndTheCountIsNot()
    {
        var (page, matches) = Bound()
            .ApplyCondition("IsActive = true")
            .ApplySorts([new Sort("Name", SortDirection.Ascending)])
            .ApplyPagination(pageSize: 2, page: 0)
            .ProjectToPaged<Minion, MinionSummary>(Configuration());

        Assert.Equal(3, matches.Count());
        Assert.Equal(["Alice Fox", "Bob Samuelson"], page.ToList().Select(row => row.Name));

        // The count half is still over the entity, which is what lets it be counted without the map
        Assert.IsAssignableFrom<IQueryable<Minion>>(matches);
    }

    [Fact]
    public void APagedMapperWorksTheSameWay()
    {
        var (page, matches) = Bound().ProjectToPaged<Minion, MinionSummary>(new Mapper(Configuration()));

        Assert.Equal(4, matches.Count());
        Assert.Equal(4, page.Count());
    }

    /// <summary>Neither half has run, exactly as BuildPaged promises</summary>
    [Fact]
    public void NeitherHalfHasRun()
    {
        var (page, matches) = Bound().ApplyPagination(pageSize: 1, page: 0).ProjectToPaged<Minion, MinionSummary>(Configuration());

        Assert.NotNull(page);
        Assert.NotNull(matches);

        // The window is on the page and not on the count, so they disagree on purpose
        Assert.Equal(1, page.Count());
        Assert.Equal(4, matches.Count());
    }

    // ---------- two answers to one question ----------

    /// <summary>
    /// A projection says a row is the keys the caller named and a DTO says a row is the DTO. Going ahead would
    /// honour the second and drop the first without saying so.
    /// </summary>
    [Fact]
    public void AnAppliedProjectionIsRefusedRatherThanIgnored()
    {
        var error = Assert.Throws<WeequeryException>(() => Bound()
            .ApplyProjection("Name, Pay")
            .ProjectTo<Minion, MinionSummary>(Configuration()));

        Assert.Contains(nameof(Inquiry<Minion>.ApplyProjection), error.Message);
        Assert.Contains("Name", error.Message);

        Assert.Throws<WeequeryException>(() => Bound()
            .ApplyProjection("Name")
            .ProjectToPaged<Minion, MinionSummary>(Configuration()));
    }

    [Fact]
    public void AndAnInquiryWithoutOneIsFine()
    {
        Assert.True(Bound().AppliedProjection.IsEmpty);
        Assert.Equal(4, Bound().ProjectTo<Minion, MinionSummary>(Configuration()).Count());
    }

    [Fact]
    public void TheArgumentsAreChecked()
    {
        Assert.Throws<WeequeryException>(() => Bound().ProjectTo<Minion, MinionSummary>((IConfigurationProvider)null!));
        Assert.Throws<WeequeryException>(() => Bound().ProjectTo<Minion, MinionSummary>((IMapper)null!));
        Assert.Throws<WeequeryException>(() => Bound().ProjectToPaged<Minion, MinionSummary>((IConfigurationProvider)null!));
    }

    // ---------- against a provider ----------

    /// <summary>
    /// The point of projecting at all: the DTO's columns and no others, read where they live rather than fetched
    /// whole and thrown away.
    /// </summary>
    [Fact]
    public void OnlyTheDtoColumnsAreRead()
    {
        var context = DBContext.GenerateMinionTestSet();

        try
        {
            var sql = context.Minions
                .WithWeequery()
                .BindProperties(Minion.Bindings)
                .ApplyCondition("IsActive = true")
                .ProjectTo<Minion, MinionSummary>(Configuration())
                .ToQueryString()
                .Replace("\r", " ")
                .Replace("\n", " ");

            var select = sql[..sql.IndexOf("FROM", StringComparison.Ordinal)];

            Assert.Contains("Name", select);
            Assert.Contains("Alias", select);
            Assert.Contains("Pay", select);

            // Filtered on, never read back
            Assert.DoesNotContain("IsActive", select);
            Assert.DoesNotContain("Morale", select);
            Assert.Contains("WHERE", sql);
        }
        finally
        {
            TestDatabase.Drop(context);
        }
    }
}
