using Mapster;
using Microsoft.EntityFrameworkCore;
using Tests.Common;
using Weequery;
using Weequery.Mapster;

namespace Tests.Unit;

/// <summary>What a caller reads a minion back as here, which is three of its columns under different names</summary>
public class MinionBrief
{
    public string Name { get; set; } = "";
    public string? Alias { get; set; }
    public decimal Salary { get; set; }
}

/// <summary>
/// Reading an Inquiry's rows back as a DTO through Mapster.
/// <para>
/// The same division of labour the AutoMapper companion has, see <see cref="AutoMapperProjectionTests"/>:
/// Weequery decides which rows and Mapster decides what a row looks like, so most of what these check is that
/// the two halves stay in their own lane. What differs is that Mapster maps by convention, so the configuration
/// is optional and a DTO whose names line up needs none at all.
/// </para>
/// </summary>
public class MapsterProjectionTests
{
    /// <summary>
    /// Scoped to the test rather than Mapster's global settings, which are process wide and would leak from one
    /// test into the next.
    /// </summary>
    private static TypeAdapterConfig Configuration()
    {
        var config = new TypeAdapterConfig();

        config.NewConfig<Minion, MinionBrief>().Map(brief => brief.Salary, minion => minion.Pay);

        return config;
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
        var rows = Bound().ApplyCondition("Name = 'Alice Fox'").ProjectTo<Minion, MinionBrief>(Configuration()).ToList();

        var row = Assert.Single(rows);

        Assert.Equal("Alice Fox", row.Name);
        Assert.Equal("Ghost", row.Alias);

        // The mapping renamed it, which is the DTO's business and not Weequery's
        Assert.Equal(12000m, row.Salary);
    }

    /// <summary>
    /// The configuration is optional, which is the one way this differs from the AutoMapper companion. Without
    /// one, the members that line up by name still map and the renamed one is left at its default.
    /// </summary>
    [Fact]
    public void NoConfigurationMapsWhatLinesUpByName()
    {
        var row = Bound().ApplyCondition("Name = 'Alice Fox'").ProjectTo<Minion, MinionBrief>().ToList().Single();

        Assert.Equal("Alice Fox", row.Name);
        Assert.Equal("Ghost", row.Alias);

        // Pay and Salary are not the same word, and nothing was told they were
        Assert.Equal(0m, row.Salary);
    }

    // ---------- the rows are Weequery's ----------

    [Fact]
    public void TheConditionStillApplies()
    {
        var rows = Bound().ApplyCondition("IsActive = true").ProjectTo<Minion, MinionBrief>(Configuration()).ToList();

        Assert.Equal(3, rows.Count);
        Assert.DoesNotContain("Charlie Smith", rows.Select(row => row.Name));
    }

    /// <summary>The sort names a binding on the entity, which the DTO need not expose at all</summary>
    [Fact]
    public void TheSortStillAppliesAndIsOverTheEntity()
    {
        var rows = Bound()
            .ApplySorts([new Sort("IsActive", SortDirection.Ascending), new Sort("Name", SortDirection.Descending)])
            .ProjectTo<Minion, MinionBrief>(Configuration())
            .ToList();

        // IsActive is nowhere on MinionBrief and still decides the order
        Assert.Equal(["Charlie Smith", "David Edgars", "Bob Samuelson", "Alice Fox"], rows.Select(row => row.Name));
    }

    [Fact]
    public void TheWindowStillApplies()
    {
        var rows = Bound()
            .ApplySorts([new Sort("Name", SortDirection.Ascending)])
            .ApplyPagination(pageSize: 2, page: 1)
            .ProjectTo<Minion, MinionBrief>(Configuration())
            .ToList();

        Assert.Equal(["Charlie Smith", "David Edgars"], rows.Select(row => row.Name));
    }

    [Fact]
    public void TheAllowListStillDecides()
    {
        Assert.Throws<WeequeryException>(() => Bound().ApplyCondition("Morale > 0").ProjectTo<Minion, MinionBrief>(Configuration()).ToList());
    }

    // ---------- paged ----------

    [Fact]
    public void ThePageIsProjectedAndTheCountIsNot()
    {
        var (page, matches) = Bound()
            .ApplyCondition("IsActive = true")
            .ApplySorts([new Sort("Name", SortDirection.Ascending)])
            .ApplyPagination(pageSize: 2, page: 0)
            .ProjectToPaged<Minion, MinionBrief>(Configuration());

        Assert.Equal(3, matches.Count());
        Assert.Equal(["Alice Fox", "Bob Samuelson"], page.ToList().Select(row => row.Name));

        // The count half is still over the entity, which is what lets it be counted without the mapping
        Assert.IsAssignableFrom<IQueryable<Minion>>(matches);
    }

    /// <summary>Neither half has run, exactly as BuildPaged promises</summary>
    [Fact]
    public void NeitherHalfHasRun()
    {
        var (page, matches) = Bound().ApplyPagination(pageSize: 1, page: 0).ProjectToPaged<Minion, MinionBrief>(Configuration());

        // The window is on the page and not on the count, so they disagree on purpose
        Assert.Equal(1, page.Count());
        Assert.Equal(4, matches.Count());
    }

    // ---------- two answers to one question ----------

    [Fact]
    public void AnAppliedProjectionIsRefusedRatherThanIgnored()
    {
        var error = Assert.Throws<WeequeryException>(() => Bound()
            .ApplyProjection("Name, Pay")
            .ProjectTo<Minion, MinionBrief>(Configuration()));

        Assert.Contains(nameof(Inquiry<Minion>.ApplyProjection), error.Message);
        Assert.Contains("Name", error.Message);

        Assert.Throws<WeequeryException>(() => Bound()
            .ApplyProjection("Name")
            .ProjectToPaged<Minion, MinionBrief>(Configuration()));
    }

    [Fact]
    public void AndAnInquiryWithoutOneIsFine()
    {
        Assert.True(Bound().AppliedProjection.IsEmpty);
        Assert.Equal(4, Bound().ProjectTo<Minion, MinionBrief>(Configuration()).Count());
    }

    [Fact]
    public void TheArgumentsAreChecked()
    {
        Assert.Throws<WeequeryException>(() => ((Inquiry<Minion>)null!).ProjectTo<Minion, MinionBrief>(Configuration()));
        Assert.Throws<WeequeryException>(() => ((Inquiry<Minion>)null!).ProjectToPaged<Minion, MinionBrief>(Configuration()));
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
                .ProjectTo<Minion, MinionBrief>(Configuration())
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
