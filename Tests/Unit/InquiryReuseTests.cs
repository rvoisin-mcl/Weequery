using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// What happens when one Inquiry is used twice, and the copy that is the way out of it.
/// <para>
/// An Inquiry is mutable and reads like it is not, which is the one place this library invites a wrong
/// assumption: a fluent chain looks like a LINQ chain, and a LINQ chain composes rather than accumulates. The
/// behaviour is pinned here so it cannot change quietly, and so the shape of the fix is written down beside it.
/// </para>
/// </summary>
public class InquiryReuseTests
{
    private static Inquiry<Minion> Bound()
    {
        return MinionTestData.Minions()
            .WithWeequery()
            .BindProperty(minion => minion.Name)
            .BindProperty(minion => minion.Pay)
            .BindProperty(minion => minion.IsActive);
    }

    // ---------- the hazard, stated so it cannot change quietly ----------

    /// <summary>
    /// The one that bites. Both builds come off the same Inquiry, so the second carries the first's filter and
    /// answers a question nobody asked, plausibly and without complaint.
    /// </summary>
    [Fact]
    public void ConditionsAccumulateAcrossBuilds()
    {
        var inquiry = Bound();

        var active = inquiry.ApplyCondition("IsActive = true").Build().Count();
        var paid = inquiry.ApplyCondition("Pay > 10000").Build().Count();

        Assert.Equal(3, active);

        // Alice and Charlie are paid over 10000, and Charlie is not active, so this is the AND of the two rather
        // than the second on its own
        Assert.Equal(1, paid);
    }

    [Fact]
    public void SortsAccumulateToo()
    {
        var inquiry = Bound().ApplySorts([new Sort("IsActive", SortDirection.Ascending)]);

        var names = inquiry.ApplySorts([new Sort("Name", SortDirection.Descending)])
            .Build()
            .ToList()
            .Select(minion => minion.Name);

        // Charlie first because he is the only inactive one, which is the first sort still applying
        Assert.Equal(["Charlie Smith", "David Edgars", "Bob Samuelson", "Alice Fox"], names);
    }

    /// <summary>Everything that is not a list is last-call-wins instead, which is its own thing to know</summary>
    [Fact]
    public void EverythingElseIsLastCallWins()
    {
        var inquiry = Bound().ApplyProjection("Name, Pay").ApplyPagination(pageSize: 3, page: 0);

        Assert.Equal(["Pay"], inquiry.ApplyProjection("Pay").BuildProjected().First().Keys);
        Assert.Equal(2, inquiry.ApplyPagination(pageSize: 2, page: 0).Build().Count());
    }

    // ---------- the way out ----------

    [Fact]
    public void ACloneCarriesTheConfigurationAndNotTheLaterChanges()
    {
        var bound = Bound();

        var active = bound.Clone().ApplyCondition("IsActive = true").Build().Count();
        var paid = bound.Clone().ApplyCondition("Pay > 10000").Build().Count();

        Assert.Equal(3, active);
        Assert.Equal(2, paid);

        // And the original was left alone by both of them
        Assert.Equal(4, bound.Build().Count());
    }

    [Fact]
    public void ACloneKeepsWhatWasAlreadyApplied()
    {
        var configured = Bound()
            .ApplyCondition("IsActive = true")
            .ApplySorts([new Sort("Name", SortDirection.Descending)])
            .ApplyPagination(pageSize: 2, page: 0)
            .ApplyProjection("Name");

        var rows = configured.Clone().BuildProjected().ToList();

        Assert.Equal(["David Edgars", "Bob Samuelson"], rows.Select(row => (string)row["Name"]!));
        Assert.Equal(["Name"], rows[0].Keys);
    }

    [Fact]
    public void ACloneCanBeBoundToWithoutTouchingTheOriginal()
    {
        var bound = Bound();

        var clone = bound.Clone().BindProperty(minion => minion.Alias);

        Assert.Single(clone.ApplyCondition("Alias = 'Ghost'").Build().ToList());
        Assert.Throws<WeequeryException>(() => bound.ApplyCondition("Alias = 'Ghost'").Build().ToList());
    }

    [Fact]
    public void ACloneCarriesCollectionsAndTheirInnerAllowList()
    {
        var crews = new List<Crew>
        {
            new() { Id = 1, Name = "Alpha", Heists = [new() { Target = "Bank", Take = 500 }] },
            new() { Id = 2, Name = "Beta", Heists = [] },
        }.AsQueryable();

        var bound = crews
            .WithWeequery()
            .BindProperty(crew => crew.Name)
            .BindCollection(crew => crew.Heists, "Heists", inner => inner.BindProperty(heist => heist.Take));

        Assert.Equal([1], bound.Clone().ApplyCondition("Heists Any (Take > 100)").Build().ToList().Select(crew => crew.Id));
    }

    [Fact]
    public void ACloneCarriesTheLenientSetting()
    {
        var lenient = Bound().IgnoreUnboundFields();

        // Dropped rather than refused, which is the setting having come along
        Assert.Equal(4, lenient.Clone().ApplyCondition("Gizmo = 3").Build().Count());

        Assert.Throws<WeequeryException>(() => Bound().Clone().ApplyCondition("Gizmo = 3").Build().ToList());
    }

    /// <summary>A build's record of what it dropped describes a build, and the copy has not built anything</summary>
    [Fact]
    public void ACloneDoesNotCarryWhatTheOriginalDropped()
    {
        var inquiry = Bound().IgnoreUnboundFields().ApplyCondition("Gizmo = 3");

        inquiry.Build().ToList();

        Assert.Single(inquiry.DroppedFields);
        Assert.Empty(inquiry.Clone().DroppedFields);
    }

    /// <summary>
    /// The lists are copied and what is in them is not, which is what makes this cheap: a binding is immutable
    /// once built, so both sets hold the same objects and neither can change the other's
    /// </summary>
    [Fact]
    public void CloningCopiesTheListsRatherThanRebuildingTheBindings()
    {
        var bound = Bound().ApplyCondition("IsActive = true");
        var clone = bound.Clone();

        // Same query, same answers, and adding to one changes nothing about the other
        Assert.Equal(bound.Build().Count(), clone.Build().Count());

        clone.ApplyCondition("Pay > 10000");

        Assert.Equal(3, bound.Build().Count());
        Assert.Equal(1, clone.Build().Count());
    }
}
