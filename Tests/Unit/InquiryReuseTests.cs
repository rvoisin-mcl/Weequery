using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// What happens when one Inquiry is used twice, which is nothing.
/// <para>
/// An Inquiry is immutable: every Apply and every Bind leaves the one it was called on as it was and hands back
/// a new one carrying the change. A fluent chain therefore composes rather than accumulates, which is what the
/// chain already looked like it did. This file pins that, because the failure it replaces was a silent one: a
/// second query built off a reused Inquiry used to carry the first one's filter and answer a question nobody
/// asked, plausibly and without complaint.
/// </para>
/// </summary>
public class InquiryReuseTests
{
    /// <summary>
    /// Whether unbound fields are dropped is a setting, so it is decided here where the query starts rather
    /// than anywhere further down the chain
    /// </summary>
    /// <param name="ignoreUnbound">see InquirySettings.IgnoreUnboundFields</param>
    private static Inquiry<Minion> Bound(bool ignoreUnbound = false)
    {
        return MinionTestData.Minions()
            .WithWeequery(InquirySettings.Default with { IgnoreUnboundFields = ignoreUnbound })
            .BindProperty(minion => minion.Name)
            .BindProperty(minion => minion.Pay)
            .BindProperty(minion => minion.IsActive);
    }

    private static List<string> Names(IQueryable<Minion> query)
    {
        return query.ToList().Select(minion => minion.Name).ToList();
    }

    // ---------- reuse, stated so it cannot change quietly ----------

    /// <summary>
    /// The one that used to bite. Both builds come off the same Inquiry and neither can see the other's filter.
    /// </summary>
    [Fact]
    public void ConditionsDoNotAccumulateAcrossBuilds()
    {
        var inquiry = Bound();

        var active = inquiry.ApplyCondition("IsActive = true").Build().Count();
        var paid = inquiry.ApplyCondition("Pay > 10000").Build().Count();

        // Alice, Bob and David are active
        Assert.Equal(3, active);

        // Alice and Charlie are paid over 10000. Under the old mutable Inquiry this was 1, being the AND of the
        // two, and nothing said so
        Assert.Equal(2, paid);

        // And the one both came off still filters on nothing at all
        Assert.Equal(4, inquiry.Build().Count());
    }

    /// <summary>
    /// The same, by the routes that take a condition already built rather than a string to parse. Each overload
    /// adds to the list in its own way, so each is asked separately.
    /// </summary>
    [Fact]
    public void ConditionsBuiltRatherThanParsedDoNotAccumulateEither()
    {
        var inquiry = Bound();

        var active = new OneValueCondition<bool>(Operator.Equals, nameof(Minion.IsActive), true);
        var paid = new OneValueCondition<decimal>(Operator.GreaterThan, nameof(Minion.Pay), 10000m);

        Assert.Equal(3, inquiry.ApplyCondition(active).Build().Count());
        Assert.Equal(2, inquiry.ApplyCondition(paid).Build().Count());

        // And the plural, which walks a list rather than taking one
        Assert.Equal(3, inquiry.ApplyConditions([active]).Build().Count());
        Assert.Equal(2, inquiry.ApplyConditions([paid]).Build().Count());

        Assert.Equal(4, inquiry.Build().Count());
    }

    /// <summary>
    /// Within one chain the sorts still compose, each breaking ties in the one before. What changed is that the
    /// Inquiry the chain started from keeps only its own.
    /// </summary>
    [Fact]
    public void SortsComposeAlongAChainAndNowhereElse()
    {
        var byActive = Bound().ApplySorts([new Sort("IsActive", SortDirection.Ascending)]);

        var byActiveThenName = byActive.ApplySorts([new Sort("Name", SortDirection.Descending)]);

        // Charlie first because he is the only inactive one, then the rest by name descending
        Assert.Equal(["Charlie Smith", "David Edgars", "Bob Samuelson", "Alice Fox"], Names(byActiveThenName.Build()));

        // The one it was built from never heard about the second sort, so the active three stay in source order
        Assert.Equal(["Charlie Smith", "Alice Fox", "Bob Samuelson", "David Edgars"], Names(byActive.Build()));
    }

    /// <summary>Everything that is not a list was last-call-wins, and is now nobody's business but the copy's</summary>
    [Fact]
    public void ReplacingAValueLeavesTheOriginalHoldingTheOldOne()
    {
        var inquiry = Bound().ApplyProjection("Name, Pay").ApplyPagination(pageSize: 3, page: 0);

        Assert.Equal(["Pay"], inquiry.ApplyProjection("Pay").BuildProjected().First().Keys);
        Assert.Equal(2, inquiry.ApplyPagination(pageSize: 2, page: 0).Build().Count());

        // Both of those made a copy and changed it, so this one still projects two fields and pages by three
        Assert.Equal(["Name", "Pay"], inquiry.BuildProjected().First().Keys);
        Assert.Equal(3, inquiry.Build().Count());
    }

    // ---------- what a copy carries ----------

    [Fact]
    public void ACopyKeepsWhatWasAlreadyApplied()
    {
        var configured = Bound()
            .ApplyCondition("IsActive = true")
            .ApplySorts([new Sort("Name", SortDirection.Descending)])
            .ApplyPagination(pageSize: 2, page: 0)
            .ApplyProjection("Name");

        var rows = configured.BuildProjected().ToList();

        Assert.Equal(["David Edgars", "Bob Samuelson"], rows.Select(row => (string)row["Name"]!));
        Assert.Equal(["Name"], rows[0].Keys);
    }

    [Fact]
    public void BindingReachesTheCopyAndNotTheOriginal()
    {
        var bound = Bound();

        var withAlias = bound.BindProperty(minion => minion.Alias);

        Assert.Single(withAlias.ApplyCondition("Alias = 'Ghost'").Build().ToList());

        // Nothing bound Alias on the one it came off, so the key is still refused there
        Assert.Throws<WeequeryException>(() => bound.ApplyCondition("Alias = 'Ghost'").Build().ToList());
    }

    [Fact]
    public void ACopyCarriesCollectionsAndTheirInnerAllowList()
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

        Assert.Equal([1], bound.ApplyCondition("Heists Any (Take > 100)").Build().ToList().Select(crew => crew.Id));
    }

    [Fact]
    public void ACopyCarriesTheLenientSetting()
    {
        var lenient = Bound(ignoreUnbound: true);

        // Dropped rather than refused, which is the setting having come along
        Assert.Equal(4, lenient.ApplyCondition("Gizmo = 3").Build().Count());

        Assert.Throws<WeequeryException>(() => Bound().ApplyCondition("Gizmo = 3").Build().ToList());
    }

    /// <summary>A build's record of what it dropped describes a build, and the copy has not built anything</summary>
    [Fact]
    public void ACopyDoesNotCarryWhatTheOriginalDropped()
    {
        var inquiry = Bound(ignoreUnbound: true).ApplyCondition("Gizmo = 3");

        inquiry.Build().ToList();

        Assert.Single(inquiry.DroppedFields);
        Assert.Empty(inquiry.ApplyPagination(10, 0).DroppedFields);
    }

    /// <summary>
    /// The lists are copied and what is in them is not, which is what makes a copy on every call affordable: a
    /// binding is immutable once built, so both sets hold the same objects and neither can change the other's
    /// </summary>
    [Fact]
    public void CopyingCopiesTheListsRatherThanRebuildingTheBindings()
    {
        var bound = Bound().ApplyCondition("IsActive = true");
        var copy = bound.ApplyPagination(10, 0);

        // Same query, same answers
        Assert.Equal(bound.Build().Count(), copy.Build().Count());

        var narrowed = copy.ApplyCondition("Pay > 10000");

        Assert.Equal(3, bound.Build().Count());
        Assert.Equal(3, copy.Build().Count());
        Assert.Equal(1, narrowed.Build().Count());
    }

    /// <summary>
    /// A binding call that refuses leaves nothing behind, because what it was assembling was never the Inquiry
    /// the caller is holding.
    /// </summary>
    [Fact]
    public void ARefusedBindingDoesNotTouchTheOriginal()
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

        // "Heists" is a collection, so a property binding cannot claim the same key
        Assert.Throws<WeequeryException>(() => bound.BindProperty(crew => crew.Name, "Heists"));

        // The collection is still bound and still answers, the failed call having changed nothing here
        Assert.Equal([1], bound.ApplyCondition("Heists Any (Take > 100)").Build().ToList().Select(crew => crew.Id));
    }
}
