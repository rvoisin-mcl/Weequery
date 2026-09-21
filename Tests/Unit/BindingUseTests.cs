using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// What a binding may be used for, see <see cref="BindingUse"/>.
/// <para>
/// A binding grants three separable things: filter on it, sort on it, read it back. The default is all three, and
/// every combination narrower than that is expressible. What these check is that each flag is honoured at every
/// site that resolves a field, and that a binding without one says so rather than reporting itself unbound.
/// </para>
/// </summary>
public class BindingUseTests
{
    private static Inquiry<Minion> Bound()
    {
        return MinionTestData.Minions()
            .WithWeequery()
            .BindProperty(minion => minion.Name)
            .BindProperty(minion => minion.Pay)
            .BindProperty(minion => minion.CauseForDeparture, "Departure", BindingUse.Projection)
            .BindProperty(minion => minion.IsVetted, "Vetted", BindingUse.Test)
            .BindProperty(minion => minion.Morale, "Morale", BindingUse.Test | BindingUse.Sort);
    }

    private static List<string> Names(string query)
    {
        return [.. Bound().ApplyCondition(query).Build().ToList().Select(minion => minion.Name)];
    }

    // ---------- the default ----------

    [Fact]
    public void ABindingGrantsAllThreeUnlessItSaysOtherwise()
    {
        Assert.Single(Names("Name = 'Alice Fox'"));
        Assert.Equal(4, Bound().ApplySorts([new Sort("Name", SortDirection.Ascending)]).Build().Count());
        Assert.Contains("Name", Bound().ApplyProjection("Name").BuildProjected().First().Keys);
    }

    [Fact]
    public void AllIsTheThreeFlagsTogether()
    {
        Assert.Equal(BindingUse.All, BindingUse.Test | BindingUse.Sort | BindingUse.Projection);
        Assert.Equal(BindingUse.None, default);
    }

    // ---------- Projection only ----------

    [Fact]
    public void ProjectionOnlyReadsBack()
    {
        var row = Bound().ApplyProjection("Name, Departure").ApplyCondition("Name = 'Bob Samuelson'").BuildProjected().Single();

        Assert.Equal(["Name", "Departure"], row.Keys);
        Assert.Equal("Eaten by shark", row["Departure"]);
    }

    [Fact]
    public void ProjectionOnlyCannotBeFilteredOn()
    {
        var error = Assert.Throws<WeequeryException>(() => Names("Departure Contains 'shark'"));

        Assert.Contains("Departure", error.Message);
        Assert.Equal(WeequeryError.OperatorUnsupported, error.Error);
        Assert.Contains(nameof(BindingUse.Projection), error.Message);
    }

    [Fact]
    public void ProjectionOnlyCannotBeSortedOn()
    {
        var error = Assert.Throws<WeequeryException>(() => Bound()
            .ApplySorts([new Sort("Departure", SortDirection.Ascending)])
            .Build()
            .ToList());

        Assert.Contains("Departure", error.Message);
    }

    /// <summary>
    /// The back door worth closing: an operand naming a bound property is a read of that property, so allowing it
    /// would let a caller learn the column by bisection, one query at a time.
    /// </summary>
    [Fact]
    public void ProjectionOnlyCannotBeComparedAgainstAsAnOperand()
    {
        var error = Assert.Throws<WeequeryException>(() => Names("Name = [Departure]"));

        Assert.Contains("Departure", error.Message);
        Assert.Equal(WeequeryError.OperatorUnsupported, error.Error);
    }

    // ---------- Condition only ----------

    [Fact]
    public void ConditionOnlyFiltersAndNothingElse()
    {
        Assert.Equal(["Alice Fox", "David Edgars"], Names("Vetted = true").Order());

        Assert.Throws<WeequeryException>(() => Bound().ApplyProjection("Vetted").BuildProjected().ToList());
        Assert.Throws<WeequeryException>(() => Bound().ApplySorts([new Sort("Vetted", SortDirection.Ascending)]).Build().ToList());
    }

    /// <summary>The case for it: a tenant or an owner column that must filter and must never be handed back</summary>
    [Fact]
    public void ConditionOnlyIsLeftOutOfAWholeRowProjection()
    {
        var keys = Bound().BuildProjected().First().Keys;

        Assert.Contains("Name", keys);
        Assert.Contains("Departure", keys);
        Assert.DoesNotContain("Vetted", keys);
    }

    [Fact]
    public void ARefusedProjectionSaysWhatItIsBoundFor()
    {
        var error = Assert.Throws<WeequeryException>(() => Bound().ApplyProjection("Vetted").BuildProjected().ToList());

        Assert.Contains("Vetted", error.Message);
        Assert.Equal(WeequeryError.OperatorUnsupported, error.Error);
        Assert.Contains(nameof(BindingUse.Test), error.Message);
    }

    // ---------- combinations ----------

    [Fact]
    public void TwoFlagsGrantExactlyThoseTwo()
    {
        Assert.Single(Names("Morale = 127"));
        Assert.Equal(4, Bound().ApplySorts([new Sort("Morale", SortDirection.Descending)]).Build().Count());

        Assert.Throws<WeequeryException>(() => Bound().ApplyProjection("Morale").BuildProjected().ToList());
    }

    [Fact]
    public void NoneGrantsNothingAtAll()
    {
        var inquiry = MinionTestData.Minions()
            .WithWeequery()
            .BindProperty(minion => minion.Name, "Name", BindingUse.None);

        Assert.Throws<WeequeryException>(() => inquiry.ApplyCondition("Name = 'Alice Fox'").Build().ToList());
        Assert.Throws<WeequeryException>(() => inquiry.ApplyProjection("Name").BuildProjected().ToList());
        Assert.Throws<WeequeryException>(() => inquiry.ApplySorts([new Sort("Name", SortDirection.Ascending)]).Build().ToList());
    }

    /// <summary>A field nobody bound at all is still reported as unbound, which is what it is</summary>
    [Fact]
    public void AnUnboundFieldStillReadsAsUnbound()
    {
        var error = Assert.Throws<WeequeryException>(() => Names("Alias = 'Ghost'"));

        Assert.Equal(WeequeryError.UnboundField, error.Error);
    }

    /// <summary>Nested as deep as you like, it is still the same field being asked about</summary>
    [Fact]
    public void NestingDoesNotSmuggleAFieldThrough()
    {
        Assert.Throws<WeequeryException>(() => Names("Pay > 1 AND (Name = 'x' OR NOT (Departure IsNull))"));
    }

    /// <summary>And neither does arriving packed rather than as text</summary>
    [Fact]
    public void APackedConditionIsCheckedToo()
    {
        var packed = ConditionFunctions.ParseQuery("Departure IsNull", QueryStyle.Native)!.Pack();

        Assert.Throws<WeequeryException>(() => Bound().ApplyCondition(packed).Build().ToList());
    }

    // ---------- where the use is carried ----------

    /// <summary>An element of a collection is the collection being named, so it inherits what that may be used for</summary>
    [Fact]
    public void AnIndexInheritsTheUseOfWhatItIndexes()
    {
        var boxes = new List<Box>
        {
            new() { Id = 1, Tallies = new() { ["apples"] = 9 } },
        }.AsQueryable();

        var inquiry = boxes
            .WithWeequery()
            .BindProperty(box => box.Id)
            .BindProperty(box => box.Tallies, "Tallies", BindingUse.Projection);

        Assert.Equal(9, inquiry.ApplyProjection("Tallies[apples]").BuildProjected().First()["Tallies[apples]"]);
        Assert.Throws<WeequeryException>(() => inquiry.ApplyCondition("Tallies[apples] > 1").Build().ToList());
    }

    [Fact]
    public void ABindingRequestCarriesTheUse()
    {
        var inquiry = MinionTestData.Minions()
            .WithWeequery()
            .BindProperties([
                new BindingRequest(nameof(Minion.Name), null),
                new BindingRequest(nameof(Minion.CauseForDeparture), "Departure", BindingUse.Projection),
            ]);

        Assert.Equal("Eaten by shark", inquiry.ApplyProjection("Departure").BuildProjected().ToList()[1]["Departure"]);
        Assert.Throws<WeequeryException>(() => inquiry.ApplyCondition("Departure IsNull").Build().ToList());
    }

    /// <summary>
    /// The kept binding sets are shared across every Inquiry built from the same requests, so the use has to be
    /// part of what tells two sets apart or the first one to be built would decide for the second
    /// </summary>
    [Fact]
    public void TheUseIsPartOfTheCacheKey()
    {
        BindingRequest[] queryable = [new(nameof(Minion.Name), "Thing")];
        BindingRequest[] projectionOnly = [new(nameof(Minion.Name), "Thing", BindingUse.Projection)];

        // Built in this order on purpose: the permissive set first, so a shared entry would let the second filter
        Assert.Single(MinionTestData.Minions().WithWeequery().BindProperties(queryable)
            .ApplyCondition("Thing = 'Alice Fox'").Build().ToList());

        Assert.Throws<WeequeryException>(() => MinionTestData.Minions().WithWeequery().BindProperties(projectionOnly)
            .ApplyCondition("Thing = 'Alice Fox'").Build().ToList());
    }

    /// <summary>
    /// A constant has no per-row value, so it never granted sorting and now says as much. The message stays the
    /// particular one rather than the general one, since being a constant is the more useful thing to be told.
    /// </summary>
    [Fact]
    public void AConstantIsDroppedFromASortRatherThanRefused()
    {
        // An Inquiry is mutable and fluent, so each of these gets its own: a sort applied to one stays on it
        static Inquiry<Minion> WithConstant()
        {
            return MinionTestData.Minions()
                .WithWeequery()
                .BindProperty(minion => minion.Pay)
                .BindConstant("Threshold", 10000m);
        }

        // Dropped rather than refused: being a constant is the more particular thing to say about it than the
        // use it was not granted, and a sort on one could not have changed the order anyway
        var sorted = WithConstant().ApplySorts([new Sort("Threshold", SortDirection.Ascending)]);

        Assert.Equal(4, sorted.Build().Count());
        Assert.Equal(BindingUse.Sort, Assert.Single(sorted.DroppedFields).From);

        // And the two it does grant still work
        Assert.Equal(2, WithConstant().ApplyCondition("Pay >= [Threshold]").Build().Count());
        Assert.Equal(10000m, WithConstant().ApplyProjection("Threshold").BuildProjected().First()["Threshold"]);
    }

    // ---------- the walker ----------

    [Fact]
    public void FieldsUsedNamesEveryFieldAConditionTouches()
    {
        var condition = ConditionFunctions.ParseQuery("Pay > [Threshold] AND (Name IsNull OR Tallies[apples] > 1)", QueryStyle.Native)!;

        Assert.Equal(["Pay", "Threshold", "Name", "Tallies[apples]"], condition.FieldsUsed());
    }

    /// <summary>
    /// Stops at a quantifier, since what is inside resolves against the collection's own allow-list and saying
    /// otherwise would report a field as bound on the entity when it is not
    /// </summary>
    [Fact]
    public void FieldsUsedStopsAtAQuantifier()
    {
        var condition = ConditionFunctions.ParseQuery("Name = 'x' AND Heists Any (Take > 1)", QueryStyle.Native)!;

        Assert.Equal(["Name", "Heists"], condition.FieldsUsed());
    }

    [Fact]
    public void FieldsUsedReadsAPackedTreeWithoutUnpackingIt()
    {
        var packed = ConditionFunctions.ParseQuery("Pay > [Threshold] AND Name IsNull", QueryStyle.Native)!.Pack();

        Assert.Equal(["Pay", "Threshold", "Name"], packed.FieldsUsed());
    }

    [Fact]
    public void FieldsUsedKeepsOneSpellingAndSurvivesNothing()
    {
        var condition = ConditionFunctions.ParseQuery("Name = 'a' OR name = 'b' OR NAME = 'c'", QueryStyle.Native)!;

        Assert.Equal(["Name"], condition.FieldsUsed());
        Assert.Empty(ConditionFunctions.FieldsUsed(null));
    }
}
