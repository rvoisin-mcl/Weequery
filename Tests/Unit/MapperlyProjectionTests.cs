using Microsoft.EntityFrameworkCore;
using Riok.Mapperly.Abstractions;
using Tests.Common;
using Weequery;
using Weequery.Mapperly;

namespace Tests.Unit;

/// <summary>What a caller reads a minion back as here, which is three of its columns under different names</summary>
public class MinionCard
{
    public string Name { get; set; } = "";
    public string? Alias { get; set; }
    public decimal Salary { get; set; }
}

/// <summary>
/// The mapper Mapperly writes the body of at compile time.
/// </summary>
/// <remarks>
/// The object mapping is here because of the rename. Mapperly refuses <c>[MapProperty]</c> on a queryable
/// projection, warning RMG065, and a projection left to map by name alone would leave Salary at zero without
/// failing. Declaring the object mapping beside it is what carries the rename into the projection, which
/// <see cref="MapperlyProjectionTests.TheRenameReachesTheProjection"/> is here to keep true.
/// </remarks>
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public partial class MinionCardMapper
{
    [MapProperty(nameof(Minion.Pay), nameof(MinionCard.Salary))]
    public partial MinionCard ToCard(Minion minion);

    public partial IQueryable<MinionCard> Project(IQueryable<Minion> minions);
}

/// <summary>
/// Reading an Inquiry's rows back as a DTO through a Mapperly generated projection.
/// <para>
/// The same division of labour the other two companions have, see <see cref="AutoMapperProjectionTests"/>, with
/// one structural difference: Mapperly has no runtime, so there is no configuration object to hand over. The
/// generated method is passed as a function, which means both type arguments are inferred and that nothing in
/// the package is Mapperly specific.
/// </para>
/// </summary>
public class MapperlyProjectionTests
{
    private static readonly MinionCardMapper Mapper = new();

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
        var rows = Bound().ApplyCondition("Name = 'Alice Fox'").ProjectTo(Mapper.Project).ToList();

        var row = Assert.Single(rows);

        Assert.Equal("Alice Fox", row.Name);
        Assert.Equal("Ghost", row.Alias);
        Assert.Equal(12000m, row.Salary);
    }

    /// <summary>
    /// Mapperly's rule rather than this package's, and pinned because getting it wrong produces a zero rather
    /// than an error: the rename lives on the object mapping beside the projection, not on the projection.
    /// </summary>
    [Fact]
    public void TheRenameReachesTheProjection()
    {
        var projected = Bound().ApplyCondition("Name = 'Alice Fox'").ProjectTo(Mapper.Project).ToList().Single();

        // The same answer the object mapping gives, which is the point of declaring it
        var mapped = Mapper.ToCard(MinionTestData.Minions().First(minion => minion.Name == "Alice Fox"));

        Assert.Equal(mapped.Salary, projected.Salary);
        Assert.NotEqual(0m, projected.Salary);
    }

    /// <summary>Neither type argument is written out, the function carrying both</summary>
    [Fact]
    public void BothTypeArgumentsAreInferred()
    {
        IQueryable<MinionCard> rows = Bound().ProjectTo(Mapper.Project);

        Assert.Equal(4, rows.Count());
    }

    // ---------- the rows are Weequery's ----------

    [Fact]
    public void TheConditionStillApplies()
    {
        var rows = Bound().ApplyCondition("IsActive = true").ProjectTo(Mapper.Project).ToList();

        Assert.Equal(3, rows.Count);
        Assert.DoesNotContain("Charlie Smith", rows.Select(row => row.Name));
    }

    /// <summary>The sort names a binding on the entity, which the DTO need not expose at all</summary>
    [Fact]
    public void TheSortStillAppliesAndIsOverTheEntity()
    {
        var rows = Bound()
            .ApplySorts([new Sort("IsActive", SortDirection.Ascending), new Sort("Name", SortDirection.Descending)])
            .ProjectTo(Mapper.Project)
            .ToList();

        // IsActive is nowhere on MinionCard and still decides the order
        Assert.Equal(["Charlie Smith", "David Edgars", "Bob Samuelson", "Alice Fox"], rows.Select(row => row.Name));
    }

    [Fact]
    public void TheWindowStillApplies()
    {
        var rows = Bound()
            .ApplySorts([new Sort("Name", SortDirection.Ascending)])
            .ApplyPagination(pageSize: 2, page: 1)
            .ProjectTo(Mapper.Project)
            .ToList();

        Assert.Equal(["Charlie Smith", "David Edgars"], rows.Select(row => row.Name));
    }

    [Fact]
    public void TheAllowListStillDecides()
    {
        Assert.Throws<WeequeryException>(() => Bound().ApplyCondition("Morale > 0").ProjectTo(Mapper.Project).ToList());
    }

    // ---------- paged ----------

    [Fact]
    public void ThePageIsProjectedAndTheCountIsNot()
    {
        var (page, matches) = Bound()
            .ApplyCondition("IsActive = true")
            .ApplySorts([new Sort("Name", SortDirection.Ascending)])
            .ApplyPagination(pageSize: 2, page: 0)
            .ProjectToPaged(Mapper.Project);

        Assert.Equal(3, matches.Count());
        Assert.Equal(["Alice Fox", "Bob Samuelson"], page.ToList().Select(row => row.Name));

        Assert.IsAssignableFrom<IQueryable<Minion>>(matches);
    }

    /// <summary>Neither half has run, exactly as BuildPaged promises</summary>
    [Fact]
    public void NeitherHalfHasRun()
    {
        var (page, matches) = Bound().ApplyPagination(pageSize: 1, page: 0).ProjectToPaged(Mapper.Project);

        Assert.Equal(1, page.Count());
        Assert.Equal(4, matches.Count());
    }

    // ---------- two answers to one question ----------

    [Fact]
    public void AnAppliedProjectionIsRefusedRatherThanIgnored()
    {
        var error = Assert.Throws<WeequeryException>(() => Bound()
            .ApplyProjection("Name, Pay")
            .ProjectTo(Mapper.Project));

        Assert.Contains(nameof(Inquiry<Minion>.ApplyProjection), error.Message);
        Assert.Contains("Name", error.Message);

        Assert.Throws<WeequeryException>(() => Bound()
            .ApplyProjection("Name")
            .ProjectToPaged(Mapper.Project));
    }

    [Fact]
    public void AndAnInquiryWithoutOneIsFine()
    {
        Assert.True(Bound().AppliedProjection.IsEmpty);
        Assert.Equal(4, Bound().ProjectTo(Mapper.Project).Count());
    }

    [Fact]
    public void TheArgumentsAreChecked()
    {
        Assert.Throws<WeequeryException>(() => Bound().ProjectTo<Minion, MinionCard>(null!));
        Assert.Throws<WeequeryException>(() => Bound().ProjectToPaged<Minion, MinionCard>(null!));
        Assert.Throws<WeequeryException>(() => ((Inquiry<Minion>)null!).ProjectTo(Mapper.Project));
    }

    /// <summary>
    /// The projection is a function this package did not write, so a null from it is caught where it happened
    /// rather than surfacing from wherever the query was eventually enumerated.
    /// </summary>
    [Fact]
    public void AProjectionReturningNullIsRefusedAtTheCall()
    {
        var error = Assert.Throws<WeequeryException>(() => Bound().ProjectTo<Minion, MinionCard>(_ => null!));

        Assert.Contains(nameof(MinionCard), error.Message);
    }

    // ---------- against a provider ----------

    /// <summary>
    /// The point of projecting at all, and the thing a generated projection has to get right to be worth using:
    /// the DTO's columns and no others, read where they live rather than fetched whole and thrown away.
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
                .ProjectTo(Mapper.Project)
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
