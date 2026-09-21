using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// Taking part of a binding back off, rather than all of it.
/// <para>
/// The counterweight to a use only ever widening, see <see cref="BindingUseTests"/>: binding grants and this is
/// the only thing that revokes. A binding narrowed to nothing is removed, so the no-argument call is what it
/// always was.
/// </para>
/// </summary>
public class RemoveBindingUseTests
{
    private static Inquiry<Minion> Bound()
    {
        return MinionTestData.Minions()
            .WithWeequery()
            .BindProperty(minion => minion.Name)
            .BindProperty(minion => minion.Pay);
    }

    // ---------- narrowing ----------

    [Fact]
    public void RevokingTheAskingLeavesTheReading()
    {
        var inquiry = Bound().RemoveBinding("Pay", BindingUse.Test | BindingUse.Sort);

        // Still readable
        Assert.Equal(12000m, inquiry.ApplyProjection("Pay").BuildProjected().First()["Pay"]);

        // And no longer askable, or sortable
        Assert.Throws<WeequeryException>(() => inquiry.ApplyCondition("Pay > 1").Build().ToList());
        Assert.Throws<WeequeryException>(() => inquiry.ApplySorts([new Sort("Pay", SortDirection.Ascending)]).Build().ToList());
    }

    [Fact]
    public void RevokingTheReadingLeavesTheAsking()
    {
        var inquiry = Bound().RemoveBinding("Pay", BindingUse.Projection);

        Assert.NotEmpty(inquiry.ApplyCondition("Pay > 1").Build().ToList());

        // A whole row projection reads what grants Projection, and Pay no longer does
        Assert.DoesNotContain("Pay", inquiry.BuildProjected().First().Keys);
    }

    /// <summary>The message still says what it is for, rather than reporting it unbound</summary>
    [Fact]
    public void ANarrowedBindingIsStillBound()
    {
        var inquiry = Bound().RemoveBinding("Pay", BindingUse.Test);

        var error = Assert.Throws<WeequeryException>(() => inquiry.ApplyCondition("Pay > 1").Build().ToList());

        Assert.Equal(WeequeryError.OperatorUnsupported, error.Error);
        Assert.Contains(nameof(BindingUse.Sort), error.Message);
        Assert.Contains(nameof(BindingUse.Projection), error.Message);
    }

    // ---------- removing ----------

    [Fact]
    public void RevokingEverythingRemovesIt()
    {
        var inquiry = Bound().RemoveBinding("Pay");

        var error = Assert.Throws<WeequeryException>(() => inquiry.ApplyCondition("Pay > 1").Build().ToList());

        Assert.Equal(WeequeryError.UnboundField, error.Error);
    }

    /// <summary>Revoked one at a time, the last one takes it away</summary>
    [Fact]
    public void RevokingTheLastUseRemovesIt()
    {
        var inquiry = Bound()
            .RemoveBinding("Pay", BindingUse.Test)
            .RemoveBinding("Pay", BindingUse.Sort)
            .RemoveBinding("Pay", BindingUse.Projection);

        var error = Assert.Throws<WeequeryException>(() => inquiry.ApplyCondition("Pay > 1").Build().ToList());

        Assert.Equal(WeequeryError.UnboundField, error.Error);
    }

    // ---------- the quiet cases ----------

    [Fact]
    public void RevokingSomethingItNeverHadChangesNothing()
    {
        var inquiry = Bound()
            .BindProperty(minion => minion.Morale, "Morale", BindingUse.Projection)
            .RemoveBinding("Morale", BindingUse.Sort);

        Assert.Equal((sbyte)5, inquiry.ApplyProjection("Morale").BuildProjected().First()["Morale"]);
    }

    [Fact]
    public void RevokingNothingIsANoOp()
    {
        var inquiry = Bound().RemoveBinding("Pay", BindingUse.None);

        Assert.NotEmpty(inquiry.ApplyCondition("Pay > 1").Build().ToList());
    }

    [Fact]
    public void AKeyNobodyBoundIsStillANoOp()
    {
        Assert.NotEmpty(Bound().RemoveBinding("Gizmo", BindingUse.Test).ApplyCondition("Pay > 1").Build().ToList());
    }

    [Fact]
    public void TheOriginalIsUntouched()
    {
        var bound = Bound();

        bound.RemoveBinding("Pay");

        Assert.NotEmpty(bound.ApplyCondition("Pay > 1").Build().ToList());
    }

    // ---------- collections, which have no use of their own ----------

    private static Inquiry<Crew> Crews()
    {
        var crews = new List<Crew>
        {
            new() { Id = 1, Name = "Alpha", Heists = [new() { Target = "Bank", Take = 500 }] },
            new() { Id = 2, Name = "Beta", Heists = [] },
        }.AsQueryable();

        return crews
            .WithWeequery()
            .BindProperty(crew => crew.Name)
            .BindCollection(crew => crew.Heists, "Heists", inner => inner.BindProperty(heist => heist.Take));
    }

    [Fact]
    public void RevokingConditionTakesACollectionAway()
    {
        var inquiry = Crews().RemoveBinding("Heists", BindingUse.Test);

        Assert.Throws<WeequeryException>(() => inquiry.ApplyCondition("Heists Any (Take > 100)").Build().ToList());
    }

    /// <summary>A quantifier is the only thing it answers, so revoking the others leaves it as it was</summary>
    [Fact]
    public void RevokingSomethingElseLeavesACollectionAlone()
    {
        var inquiry = Crews().RemoveBinding("Heists", BindingUse.Projection | BindingUse.Sort);

        Assert.Equal([1], inquiry.ApplyCondition("Heists Any (Take > 100)").Build().ToList().Select(crew => crew.Id));
    }
}
