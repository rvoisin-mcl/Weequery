using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// Binding a collection and letting resolution write the inside, rather than declaring it.
/// <para>
/// The same call and the same result as the configuring form; what differs is who wrote the element's
/// allow-list. So most of these are that equivalence, and the rest are the depth, which is counted from the
/// element rather than from the entity and is the only thing about it that is not obvious.
/// </para>
/// </summary>
public class ResolvedCollectionTests
{
    private static IQueryable<Box> Boxes()
    {
        return new List<Box>
        {
            new() { Id = 1, Slots = [new() { Weight = 50, Label = "heavy" }, new() { Weight = 5, Label = "light" }] },
            new() { Id = 2, Slots = [new() { Weight = 5, Label = "light" }] },
            new() { Id = 3, Slots = [] },
            new() { Id = 4 },
        }.AsQueryable();
    }

    private static Inquiry<Minion> Assignments(int? maxDepth = null)
    {
        var inquiry = MinionTestData.Minions().WithWeequery().BindProperties(Minion.Bindings);

        return (maxDepth is null)
            ? inquiry.BindCollection(minion => minion.LairAssignments!, "Assignments")
            : inquiry.BindCollection(minion => minion.LairAssignments!, "Assignments", maxDepth.Value);
    }

    // ---------- the same thing, written by somebody else ----------

    /// <summary>
    /// The property this exists for: what resolution writes is what you would have declared
    /// </summary>
    [Fact]
    public void ResolvingGivesWhatDeclaringGives()
    {
        static List<int> Ask(Inquiry<Box> inquiry)
        {
            return [.. inquiry.ApplyCondition("Slots Any (Weight > 10)").Build().Select(box => box.Id)];
        }

        var declared = Ask(Boxes().WithWeequery()
            .BindProperty(box => box.Id)
            .BindCollection(box => box.Slots!, "Slots", inner => inner
                .BindProperty(slot => slot.Weight)
                .BindProperty(slot => slot.Label)));

        var resolved = Ask(Boxes().WithWeequery()
            .BindProperty(box => box.Id)
            .BindCollection(box => box.Slots!, "Slots"));

        Assert.Equal([1], declared);
        Assert.Equal(declared, resolved);
    }

    [Fact]
    public void AResolvedCollectionAnswersEveryQuantifier()
    {
        static List<int> Ask(string query)
        {
            return [.. Boxes().WithWeequery().BindProperty(box => box.Id).BindCollection(box => box.Slots!, "Slots")
                .ApplyCondition(query).Build().Select(box => box.Id)];
        }

        Assert.Equal([1], Ask("Slots Any (Weight > 10)"));
        Assert.Equal([1, 2], Ask("Slots Any (Label = 'light')"));
        Assert.Equal([2, 3, 4], Ask("Slots None (Weight > 10)"));
        Assert.Equal([2, 3, 4], Ask("Slots All (Weight < 10)"));   // true of the empty one and of the missing one
    }

    // ---------- the depth is the element's ----------

    /// <summary>
    /// Zero is the element's own columns, which on a link table is a pair of ids and the navigations, and rarely
    /// the question anybody had
    /// </summary>
    [Fact]
    public void ZeroStopsAtTheElement()
    {
        var shallow = Assignments(0).ApplyCondition("Assignments Any (Lair IsNull)");

        Assert.True(shallow.Validate().IsValid);

        var reaching = Assignments(0).ApplyCondition("Assignments Any (Lair.Name StartsWith 'V')");

        Assert.False(reaching.Validate().IsValid);
    }

    /// <summary>
    /// And the default reaches through it, which is the second hop of the join the collection stands for
    /// </summary>
    [Fact]
    public void TheDefaultReachesThroughTheElement()
    {
        Assert.True(Assignments().ApplyCondition("Assignments Any (Lair.Name StartsWith 'V')").Validate().IsValid);
    }

    /// <summary>
    /// The same default <see cref="Inquiry{T}.ResolveBindables"/> takes, this being the same walk one level down
    /// </summary>
    [Fact]
    public void TheDefaultIsTheOneResolveBindablesTakes()
    {
        var byDefault = Assignments().ApplyCondition("Assignments Any (Lair.Name StartsWith 'V')");
        var spelled = Assignments(1).ApplyCondition("Assignments Any (Lair.Name StartsWith 'V')");

        Assert.Equal(byDefault.Validate().IsValid, spelled.Validate().IsValid);
        Assert.True(spelled.Validate().IsValid);
    }

    [Theory]
    [InlineData(-3)]
    [InlineData(300)]
    public void TheDepthIsBounded(int maxDepth)
    {
        Assert.True(Assignments(maxDepth).ApplyCondition("Assignments Any (Lair IsNull)").Validate().IsValid);
    }

    // ---------- what it refuses ----------

    /// <summary>
    /// An element resolving to nothing is the refusal declaring an empty set already gives, rather than a
    /// second way of saying it
    /// </summary>
    [Fact]
    public void AnElementResolvingToNothingIsRefused()
    {
        var settings = BindingResolutionSettings.Default with { IgnorePaths = ["Weight", "Label"] };

        var error = Assert.Throws<WeequeryException>(() =>
            Boxes().WithWeequery().BindCollection(box => box.Slots!, "Slots", 0, settings));

        Assert.Equal(WeequeryError.BindingInvalid, error.Error);
        Assert.Contains("Slots", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// And a key already taken is refused the same way the declaring form refuses it
    /// </summary>
    [Fact]
    public void AKeyAlreadyTakenIsRefused()
    {
        var error = Assert.Throws<WeequeryException>(() =>
            Boxes().WithWeequery()
                .BindProperty(box => box.Id, "Slots")
                .BindCollection(box => box.Slots!, "Slots"));

        Assert.Equal(WeequeryError.KeyTaken, error.Error);
    }

    // ---------- settings ----------

    /// <summary>
    /// The settings reach inside, and the paths they name there are the element's own rather than the entity's
    /// </summary>
    [Fact]
    public void SettingsAreMatchedInsideTheElement()
    {
        var settings = BindingResolutionSettings.Default with { IgnorePaths = ["Label"] };

        var inquiry = Boxes().WithWeequery()
            .BindProperty(box => box.Id)
            .BindCollection(box => box.Slots!, "Slots", 0, settings);

        Assert.True(inquiry.ApplyCondition("Slots Any (Weight > 10)").Validate().IsValid);
        Assert.False(inquiry.ApplyCondition("Slots Any (Label = 'heavy')").Validate().IsValid);
    }

    /// <summary>
    /// Nothing about the outer allow-list changed: the inside is still only reachable through the quantifier
    /// </summary>
    [Fact]
    public void TheInsideIsStillNotNameableOutside()
    {
        var inquiry = Boxes().WithWeequery().BindProperty(box => box.Id).BindCollection(box => box.Slots!, "Slots");

        Assert.False(inquiry.ApplyCondition("Weight > 10").Validate().IsValid);
    }
}
