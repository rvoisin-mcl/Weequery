using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// Subtracting by wildcard rather than one name at a time.
/// <para>
/// The same wildcards a projection takes, and deliberately the same code deciding what they match, so that
/// "Lair.*" cannot come to mean one thing when read and another when removed. What is new is the third shape:
/// a path into a collection, which is the only way to subtract in there, an element's keys not being keys of
/// the Inquiry.
/// </para>
/// </summary>
public class RemoveBindingWildcardTests
{
    private static Inquiry<Minion> Resolved()
    {
        return MinionTestData.Minions().WithWeequery().BindResolve();
    }

    private static IEnumerable<string> Keys(Inquiry<Minion> inquiry)
    {
        return inquiry.ListBindings().Where(entry => entry.ElementOf is null).Select(entry => entry.Key);
    }

    private static IEnumerable<string> Inside(Inquiry<Minion> inquiry, string collection)
    {
        return inquiry.ListBindings().Where(entry => entry.ElementOf == collection).Select(entry => entry.ElementKey!);
    }

    // ---------- across the entity ----------

    /// <summary>
    /// A trailing .* is everything under a branch and not the branch itself, exactly as a projection reads it,
    /// so what is left is still testable for null
    /// </summary>
    [Fact]
    public void ABranchWildcardLeavesTheBranch()
    {
        var inquiry = MinionTestData.Minions().WithWeequery()
            .BindProperty("LairAssignments[0].Lair", "Lair")
            .BindProperty("LairAssignments[0].Lair.Name", "Lair.Name")
            .BindProperty("LairAssignments[0].Lair.Capacity", "Lair.Capacity")
            .BindProperty(minion => minion.Name)
            .RemoveBinding("Lair.*");

        Assert.Equal(["Lair", "Name"], Keys(inquiry));
        Assert.True(inquiry.ApplyCondition("Lair IsNull").Validate().IsValid);
    }

    /// <summary>
    /// And the prefix keeps its dot, so a branch cannot sweep in a name that merely starts the same way
    /// </summary>
    [Fact]
    public void ABranchWildcardDoesNotSweepInANeighbour()
    {
        var inquiry = MinionTestData.Minions().WithWeequery()
            .BindProperty(minion => minion.Name, "Lair.Name")
            .BindProperty(minion => minion.Alias, "Lairyard")
            .RemoveBinding("Lair.*");

        Assert.Equal(["Lairyard"], Keys(inquiry));
    }

    [Fact]
    public void TheBareWildcardTakesAllOfIt()
    {
        var inquiry = Resolved().RemoveBinding("*");

        Assert.Empty(inquiry.ListBindings());
    }

    /// <summary>
    /// A wildcard narrows where a name narrows, rather than always removing
    /// </summary>
    [Fact]
    public void AWildcardNarrowsTheUseTheSameWay()
    {
        var inquiry = Resolved().RemoveBinding("*", BindingUse.Sort);

        Assert.NotEmpty(Keys(inquiry));
        Assert.All(inquiry.ListBindings().Where(entry => entry.ElementOf is null),
            entry => Assert.False(entry.Use.HasFlag(BindingUse.Sort)));

        // And a subtraction that is not a Test leaves every collection and every element alone
        Assert.NotEmpty(Inside(inquiry, "LairAssignments"));
        Assert.True(inquiry.ListBindings().Single(entry => entry.Key == "LairAssignments").IsCollection);
    }

    /// <summary>
    /// Matching nothing is a subtraction that subtracted nothing, which is not an error the way it is in a
    /// projection: there is no quiet wrong answer to warn about
    /// </summary>
    [Fact]
    public void AWildcardMatchingNothingIsNotAnError()
    {
        var before = Keys(Resolved()).ToList();

        Assert.Equal(before, Keys(Resolved().RemoveBinding("Nowhere.*")));
        Assert.Equal(before, Keys(Resolved().RemoveBinding("Nowhere[].*")));
    }

    // ---------- inside a collection ----------

    /// <summary>
    /// The example this exists for: everything inside a collection, in the spelling ListBindings reports.
    /// <para>
    /// Emptying the inside takes the collection with it, an empty allow-list answering no condition at all. The
    /// property binding is untouched, so the key stays for a null test and an index.
    /// </para>
    /// </summary>
    [Fact]
    public void TheCollectionWildcardUnbindsEverythingInside()
    {
        var inquiry = Resolved().RemoveBinding("LairAssignments[].*");

        Assert.Empty(Inside(inquiry, "LairAssignments"));

        var entry = inquiry.ListBindings().Single(listed => listed.Key == "LairAssignments");

        Assert.False(entry.IsCollection);
        Assert.Equal(BindingUse.All, entry.Use);

        // So the quantifier is gone and the index is not
        Assert.False(inquiry.ApplyCondition("LairAssignments Any (LairID IsNotNull)").Validate().IsValid);
        Assert.True(inquiry.ApplyCondition("LairAssignments[0] IsNotNull").Validate().IsValid);
    }

    /// <summary>
    /// A branch inside one, which is the second hop of a link table and the half worth subtracting
    /// </summary>
    [Fact]
    public void ABranchInsideACollectionLeavesTheRest()
    {
        var inquiry = Resolved().RemoveBinding("LairAssignments[].Minion.*");

        var inside = Inside(inquiry, "LairAssignments").ToList();

        Assert.Contains("Minion", inside);
        Assert.Contains("Lair.Name", inside);
        Assert.DoesNotContain(inside, key => key.StartsWith("Minion.", StringComparison.Ordinal));

        // Still a collection, there being plenty left to ask about
        Assert.True(inquiry.ApplyCondition("LairAssignments Any (Lair.Name StartsWith 'V')").Validate().IsValid);
        Assert.False(inquiry.ApplyCondition("LairAssignments Any (Minion.Pay > 1)").Validate().IsValid);
    }

    /// <summary>
    /// And one name inside one, the qualified spelling being addressable without a wildcard on it
    /// </summary>
    [Fact]
    public void OneNameInsideACollectionIsSubtractedOnItsOwn()
    {
        var inquiry = Resolved().RemoveBinding("LairAssignments[].Lair.Name");

        var inside = Inside(inquiry, "LairAssignments").ToList();

        Assert.DoesNotContain("Lair.Name", inside);
        Assert.Contains("Lair.Capacity", inside);
    }

    /// <summary>
    /// A subtraction that does not include Test leaves the inside alone, an element having no other use to lose
    /// </summary>
    [Fact]
    public void TheInsideIsOnlyTouchedByATestSubtraction()
    {
        var inquiry = Resolved().RemoveBinding("LairAssignments[].*", BindingUse.Projection | BindingUse.Sort);

        Assert.NotEmpty(Inside(inquiry, "LairAssignments"));
    }

    // ---------- the batch ----------

    /// <summary>
    /// The batch takes the same shapes, and they add up
    /// </summary>
    [Fact]
    public void TheBatchTakesTheSameShapes()
    {
        var inquiry = Resolved().RemoveBindings(["LairAssignments[].Minion.*", "Pay", "IsVetted"]);

        var keys = Keys(inquiry).ToList();

        Assert.DoesNotContain("Pay", keys);
        Assert.DoesNotContain("IsVetted", keys);
        Assert.Contains("Name", keys);
        Assert.DoesNotContain(Inside(inquiry, "LairAssignments"), key => key.StartsWith("Minion.", StringComparison.Ordinal));
    }

    /// <summary>
    /// One call and several are the same thing, the batch being what the single now goes through
    /// </summary>
    [Fact]
    public void OneCallAndSeveralAgree()
    {
        var batched = Resolved().RemoveBindings(["Pay", "LairAssignments[].Minion.*"]);
        var chained = Resolved().RemoveBinding("Pay").RemoveBinding("LairAssignments[].Minion.*");

        Assert.Equal(Keys(chained), Keys(batched));
        Assert.Equal(Inside(chained, "LairAssignments"), Inside(batched, "LairAssignments"));
    }

    [Fact]
    public void AnEmptyOrNullKeyIsStillRefused()
    {
        Assert.Throws<WeequeryException>(() => Resolved().RemoveBinding(""));
        Assert.Throws<WeequeryException>(() => Resolved().RemoveBindings(["Pay", ""]));
        Assert.Throws<WeequeryException>(() => Resolved().RemoveBindings(null!));
    }

    /// <summary>
    /// And the original is untouched, this being a copy like every other binding call
    /// </summary>
    [Fact]
    public void TheOriginalKeepsWhatWasTakenFromTheCopy()
    {
        var bound = Resolved();
        var stripped = bound.RemoveBinding("LairAssignments[].*");

        Assert.Empty(Inside(stripped, "LairAssignments"));
        Assert.NotEmpty(Inside(bound, "LairAssignments"));
    }
}
