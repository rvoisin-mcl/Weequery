using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// <c>*</c> and <c>Prefix.*</c> in a projection.
/// <para>
/// A wildcard is a way of naming the allow-list rather than a way around it: whatever it expands to is still
/// only what grants <see cref="BindingUse.Projection"/>, which is what these mostly check.
/// </para>
/// </summary>
public class ProjectionWildcardTests
{
    private static Inquiry<LairAssignment> Bound()
    {
        var rows = new List<LairAssignment>
        {
            new()
            {
                LairID = Guid.Empty,
                MinionID = Guid.Empty,
                Lair = new Lair { Name = "Volcano", Capacity = 40 },
            },
        }.AsQueryable();

        return rows
            .WithWeequery()
            .BindProperty(assignment => assignment.LairID)
            .BindProperty(assignment => assignment.Lair!.Name, "Lair.Name")
            .BindProperty(assignment => assignment.Lair!.Capacity, "Lair.Capacity");
    }

    private static List<string> Keys(Inquiry<LairAssignment> inquiry)
    {
        return [.. inquiry.BuildProjected().First().Keys];
    }

    // ---------- everything ----------

    [Fact]
    public void AStarReadsEveryProjectableField()
    {
        Assert.Equal(["LairID", "Lair.Name", "Lair.Capacity"], Keys(Bound().ApplyProjection("*")));
    }

    /// <summary>The same set a projection naming nothing gives, which is what it is shorthand for</summary>
    [Fact]
    public void AStarMatchesWhatNamingNothingGives()
    {
        Assert.Equal(Keys(Bound()), Keys(Bound().ApplyProjection("*")));
    }

    [Fact]
    public void AStarSkipsWhatIsNotBoundForProjection()
    {
        var inquiry = Bound()
            .BindProperty(assignment => assignment.MinionID, "MinionID", BindingUse.Test)
            .ApplyProjection("*");

        Assert.DoesNotContain("MinionID", Keys(inquiry));
    }

    // ---------- under a prefix ----------

    [Fact]
    public void APrefixReadsEveryFieldUnderIt()
    {
        Assert.Equal(["Lair.Name", "Lair.Capacity"], Keys(Bound().ApplyProjection("Lair.*")));
    }

    [Fact]
    public void APrefixSkipsWhatIsNotBoundForProjection()
    {
        var inquiry = Bound()
            .RemoveBinding("Lair.Capacity", BindingUse.Projection)
            .ApplyProjection("Lair.*");

        Assert.Equal(["Lair.Name"], Keys(inquiry));
    }

    /// <summary>
    /// The dot stays on the prefix, so a key that merely begins with the same letters is not swept in.
    /// </summary>
    [Fact]
    public void APrefixStopsAtTheDot()
    {
        var rows = new List<Lair> { new() { Name = "Volcano", Capacity = 40 } }.AsQueryable();

        var inquiry = rows
            .WithWeequery()
            .BindProperty(lair => lair.Name, "Lair.Name")
            .BindProperty(lair => lair.Capacity, "Lairyard.Capacity")
            .ApplyProjection("Lair.*");

        Assert.Equal(["Lair.Name"], [.. inquiry.BuildProjected().First().Keys]);
    }

    // ---------- composing ----------

    [Fact]
    public void ANamedFieldAndAPrefixComposeInTheOrderAsked()
    {
        Assert.Equal(["LairID", "Lair.Name", "Lair.Capacity"], Keys(Bound().ApplyProjection("LairID, Lair.*")));
    }

    [Fact]
    public void AKeyAskedForTwiceIsReadOnce()
    {
        Assert.Equal(["Lair.Name", "Lair.Capacity"], Keys(Bound().ApplyProjection("Lair.Name, Lair.*")));
    }

    /// <summary>The same fields, though not in the same order: a named one keeps the place it was asked in</summary>
    [Fact]
    public void AStarBesideNamesIsStillEverything()
    {
        var everything = Keys(Bound().ApplyProjection("*"));
        var named = Keys(Bound().ApplyProjection("Lair.Name, *"));

        Assert.Equal(everything.Order(), named.Order());

        // and Lair.Name led, because that is where the caller put it
        Assert.Equal("Lair.Name", named[0]);
    }

    // ---------- a prefix that finds nothing ----------

    [Fact]
    public void APrefixMatchingNothingIsRefused()
    {
        var error = Assert.Throws<WeequeryException>(() => Keys(Bound().ApplyProjection("Gizmo.*")));

        Assert.Equal(WeequeryError.UnboundField, error.Error);
        Assert.Contains("Gizmo.", error.Message);
    }

    [Fact]
    public void APrefixMatchingNothingIsDroppedWhereThatWasAskedFor()
    {
        var inquiry = Bound().IgnoreUnboundFields().ApplyProjection("LairID, Gizmo.*");

        Assert.Equal(["LairID"], Keys(inquiry));
        Assert.Equal("Gizmo.*", Assert.Single(inquiry.DroppedFields).Field);
    }

    // ---------- what it is not ----------

    /// <summary>A wildcard is expanded at build, so what the caller wrote is what travels</summary>
    [Fact]
    public void TheWildcardSurvivesARoundTrip()
    {
        var projection = Projection.Parse("Lair.*");

        Assert.Equal(projection.Fields, Projection.Parse(projection.ToQuery()).Fields);
    }

    /// <summary>Naming one is still naming a projection, so a DTO is still two answers to one question</summary>
    [Fact]
    public void AStarIsNotTheSameAsHavingNoProjection()
    {
        Assert.False(Bound().ApplyProjection("*").AppliedProjection.IsEmpty);
        Assert.True(Bound().AppliedProjection.IsEmpty);
    }
}
