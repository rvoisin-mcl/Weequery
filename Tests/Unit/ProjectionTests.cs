using System.Text.Json;
using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// Projections: reading back the fields a caller named rather than whole entities.
/// <para>
/// The allow-list is the one the conditions already use, so most of what these check is the shape of a row: which
/// keys it holds, how they are spelled, and what a null becomes.
/// </para>
/// </summary>
public class ProjectionTests
{
    private static Inquiry<Minion> Bound()
    {
        return MinionTestData.Minions()
            .WithWeequery()
            .BindProperty(minion => minion.Name)
            .BindProperty(minion => minion.Alias)
            .BindProperty(minion => minion.Pay)
            .BindProperty(minion => minion.IsActive)
            .BindProperty(minion => minion.FireDate);
    }

    private static List<Dictionary<string, object?>> Rows(string? fields, string? condition = null)
    {
        var inquiry = Bound().ApplyProjection(fields);

        if (condition is not null) { inquiry = inquiry.ApplyCondition(condition); }

        return [.. inquiry.BuildProjected()];
    }

    // ---------- the shape of a row ----------

    [Fact]
    public void ARowHoldsTheFieldsAsked()
    {
        var rows = Rows("Name, Pay", "Name = 'Alice Fox'");

        var row = Assert.Single(rows);

        Assert.Equal(["Name", "Pay"], row.Keys);
        Assert.Equal("Alice Fox", row["Name"]);
        Assert.Equal(12000m, row["Pay"]);
    }

    /// <summary>
    /// The allow-list decided what the key is called, so that is what comes back. Two callers typing it
    /// differently get the same shape, which is what anything deserializing it needs.
    /// </summary>
    [Fact]
    public void KeysComeBackAsTheBindingSpelledThem()
    {
        foreach (var asked in new[] { "name", "NAME", "NaMe" })
        {
            var row = Rows(asked, "Name = 'Alice Fox'").Single();

            Assert.Equal(["Name"], row.Keys);
        }
    }

    [Fact]
    public void TheOrderAskedForIsTheOrderKept()
    {
        Assert.Equal(["Pay", "Name"], Rows("Pay, Name").First().Keys);
        Assert.Equal(["Name", "Pay"], Rows("Name, Pay").First().Keys);
    }

    /// <summary>A projection is a set of columns, and a dictionary holds each key once</summary>
    [Fact]
    public void AFieldNamedTwiceIsKeptOnce()
    {
        Assert.Equal(["Name", "Pay"], Rows("Name, Pay, name").First().Keys);
    }

    /// <summary>Nothing asked for is everything allowed, which is the only other thing it could mean</summary>
    [Fact]
    public void NoProjectionReadsEveryBoundField()
    {
        var row = Rows(null).First();

        Assert.Equal(["Alias", "FireDate", "IsActive", "Name", "Pay"], row.Keys.Order(), StringComparer.Ordinal);
    }

    [Fact]
    public void AnEmptyProjectionIsTheSameAsNone()
    {
        Assert.Equal(Rows(null).First().Count, Rows("   ").First().Count);
    }

    /// <summary>The last call wins, a projection being one list of columns rather than something that accumulates</summary>
    [Fact]
    public void ApplyingTwiceKeepsTheSecond()
    {
        var row = Bound().ApplyProjection("Name, Pay").ApplyProjection("Alias").BuildProjected().First();

        Assert.Equal(["Alias"], row.Keys);
    }

    [Fact]
    public void AProjectionCanBeGivenAsKeys()
    {
        var row = Bound().ApplyProjection(["Pay", "Name"]).BuildProjected().First();

        Assert.Equal(["Pay", "Name"], row.Keys);
    }

    // ---------- values ----------

    [Fact]
    public void ANullValueComesBackAsNull()
    {
        // Bob has no alias and Alice does
        var rows = Rows("Name, Alias").ToDictionary(row => (string)row["Name"]!, row => row["Alias"]);

        Assert.Null(rows["Bob Samuelson"]);
        Assert.Equal("Ghost", rows["Alice Fox"]);
    }

    /// <summary>A nullable value type boxes to null rather than to its default, which is the whole difference</summary>
    [Fact]
    public void ANullableValueTypeBoxesToNull()
    {
        var rows = Rows("Name, FireDate").ToDictionary(row => (string)row["Name"]!, row => row["FireDate"]);

        Assert.Null(rows["Alice Fox"]);
        Assert.Equal(new DateTime(2024, 12, 25), rows["Bob Samuelson"]);
    }

    [Fact]
    public void AValueKeepsItsOwnType()
    {
        var row = Rows("Pay, IsActive, Name", "Name = 'Alice Fox'").Single();

        Assert.IsType<decimal>(row["Pay"]);
        Assert.IsType<bool>(row["IsActive"]);
        Assert.IsType<string>(row["Name"]);
    }

    /// <summary>A path that runs through a null reads as a null rather than throwing</summary>
    [Fact]
    public void APathThroughANullIsGuarded()
    {
        var boxes = new List<Box>
        {
            new() { Id = 1, Slots = [new() { Weight = 50, Label = "heavy" }] },
            new() { Id = 2 },     // no slots at all
        }.AsQueryable();

        var rows = boxes
            .WithWeequery()
            .BindProperty(box => box.Id)
            .BindProperty(box => box.Slots![0].Weight, "FirstWeight")
            .BindProperty(box => box.Slots![0].Label, "FirstLabel")
            .ApplyProjection("Id, FirstWeight, FirstLabel")
            .BuildProjected()
            .ToList();

        Assert.Equal(50, rows[0]["FirstWeight"]);
        Assert.Equal("heavy", rows[0]["FirstLabel"]);

        Assert.Null(rows[1]["FirstWeight"]);
        Assert.Null(rows[1]["FirstLabel"]);
    }

    /// <summary>An element nothing sits at is a null here too, which is the rule everywhere else</summary>
    [Fact]
    public void AnIndexCanBeProjected()
    {
        var boxes = new List<Box>
        {
            new() { Id = 1, Tallies = new() { ["apples"] = 9 } },
            new() { Id = 2, Tallies = new() { ["pears"] = 1 } },
        }.AsQueryable();

        var rows = boxes
            .WithWeequery()
            .BindProperty(box => box.Tallies)
            .ApplyProjection("Tallies[apples]")
            .BuildProjected()
            .ToList();

        Assert.Equal(["Tallies[apples]"], rows[0].Keys);
        Assert.Equal(9, rows[0]["Tallies[apples]"]);
        Assert.Null(rows[1]["Tallies[apples]"]);
    }

    /// <summary>A constant is a value the application supplied, and it projects as one</summary>
    [Fact]
    public void AConstantProjects()
    {
        var row = MinionTestData.Minions()
            .WithWeequery()
            .BindProperty(minion => minion.Name)
            .BindConstant("Tenant", 42)
            .ApplyProjection("Name, Tenant")
            .BuildProjected()
            .First();

        Assert.Equal(42, row["Tenant"]);
    }

    // ---------- the allow-list ----------

    [Fact]
    public void AFieldNobodyBoundIsRefused()
    {
        var error = Assert.Throws<WeequeryException>(() => Rows("Name, Morale"));

        Assert.Contains("Morale", error.Message);
    }

    /// <summary>Bound, so "unbound" would be a lie, and there is no single value to read</summary>
    [Fact]
    public void ACollectionCannotBeProjected()
    {
        var error = Assert.Throws<WeequeryException>(() => new List<Crew>().AsQueryable()
            .WithWeequery()
            .BindProperty(crew => crew.Name)
            .BindCollection(crew => crew.Heists, "Heists", inner => inner.BindProperty(heist => heist.Take))
            .ApplyProjection("Name, Heists")
            .BuildProjected()
            .ToList());

        Assert.Contains("Heists", error.Message);
        Assert.Equal(WeequeryError.OperatorUnsupported, error.Error);
    }

    /// <summary>Filtering and projecting are separate: a field can be named in either, or both, or neither</summary>
    [Fact]
    public void AProjectionDoesNotHaveToNameWhatTheConditionDid()
    {
        var rows = Rows("Name", "Pay > 10000");

        Assert.Equal(["Alice Fox", "Charlie Smith"], rows.Select(row => (string)row["Name"]!).Order());
        Assert.Equal(["Name"], rows.First().Keys);
    }

    // ---------- the text ----------

    [Fact]
    public void AProjectionWritesBackAsItReads()
    {
        Assert.Equal("[Name], [Pay]", Projection.Parse("Name, Pay").ToQuery());
        Assert.Equal("[Tallies][apples]", Projection.Parse("Tallies[apples]").ToQuery());
        Assert.Equal("'Total Pay'", Projection.Parse("'Total Pay'").ToQuery());
        Assert.Equal(string.Empty, Projection.Parse(null).ToQuery());
    }

    [Fact]
    public void TheRoundTripIsStable()
    {
        foreach (var text in new[] { "Name, Pay", "[Name], [Pay]", "Tallies[apples], Name", "'Total Pay'" })
        {
            var once = Projection.Parse(text).ToQuery();

            Assert.Equal(once, Projection.Parse(once).ToQuery());
        }
    }

    [Fact]
    public void AMalformedListIsRefused()
    {
        Assert.Throws<WeequeryException>(() => Projection.Parse("Name,"));
        Assert.Throws<WeequeryException>(() => Projection.Parse("Name Pay"));
        Assert.Throws<WeequeryException>(() => Projection.Parse("Name, [Pay"));
        Assert.Throws<WeequeryException>(() => Projection.Parse("Tallies[]"));
    }

    [Fact]
    public void NothingNamedIsTheEmptyProjection()
    {
        Assert.True(Projection.Parse(null).IsEmpty);
        Assert.True(Projection.Parse("   ").IsEmpty);
        Assert.True(Projection.Of(null).IsEmpty);
        Assert.True(Projection.Of([]).IsEmpty);
        Assert.Same(Projection.None, Projection.Parse(""));
    }

    // ---------- paging, and the wire ----------

    /// <summary>
    /// The projection decides what a row says, not which rows there are, so the count is the count either way
    /// </summary>
    [Fact]
    public void OnlyThePageIsProjectedAndTheCountIsUnchanged()
    {
        var (page, matches) = Bound()
            .ApplyCondition("IsActive = true")
            .ApplySorts([new Sort("Name", SortDirection.Ascending)])
            .ApplyPagination(pageSize: 2, page: 0)
            .ApplyProjection("Name")
            .BuildPagedProjected();

        var rows = page.ToList();

        Assert.Equal(3, matches.Count());
        Assert.Equal(2, rows.Count);
        Assert.Equal(["Alice Fox", "Bob Samuelson"], rows.Select(row => (string)row["Name"]!));
        Assert.Equal(["Name"], rows.First().Keys);
    }

    [Fact]
    public void ATransportCarriesAProjection()
    {
        var sent = new TransportCondition("IsActive = true") { Projection = "Name, Pay" };

        var read = JsonSerializer.Deserialize<TransportCondition>(JsonSerializer.Serialize(sent))!;

        Assert.Equal(["Name", "Pay"], read.UnpackProjection().Fields);

        var rows = Bound().ApplyCondition(read.Unpack()).ApplyProjection(read.UnpackProjection()).BuildProjected().ToList();

        Assert.Equal(3, rows.Count);
        Assert.Equal(["Name", "Pay"], rows.First().Keys);
    }

    /// <summary>A payload that names none is the payload it always was, and applying it changes nothing</summary>
    [Fact]
    public void ATransportWithoutOneSaysNothingAboutIt()
    {
        var sent = new TransportCondition("IsActive = true");

        var json = JsonSerializer.Serialize(sent);

        Assert.DoesNotContain("Name", json);
        Assert.True(JsonSerializer.Deserialize<TransportCondition>(json)!.UnpackProjection().IsEmpty);
    }
}
