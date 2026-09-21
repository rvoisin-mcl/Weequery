using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// Dropping the parts of a query that name a field nothing bound, rather than refusing the whole query.
/// <para>
/// For the stale saved filter: a caller stored a query, a binding has since gone, and refusing the lot would
/// leave them unable to open their own view to fix it. Off by default, because a filter that quietly stops
/// filtering is worse than one that refuses out loud.
/// </para>
/// </summary>
public class IgnoreUnboundFieldsTests
{
    /// <summary>
    /// Whether unbound fields are dropped is a setting, so it is decided here where the query starts rather than
    /// anywhere further down the chain
    /// </summary>
    /// <param name="ignore">see InquirySettings.IgnoreUnboundFields</param>
    private static Inquiry<Minion> Bound(bool ignore)
    {
        return MinionTestData.Minions()
            .WithWeequery(InquirySettings.Default with { IgnoreUnboundFields = ignore })
            .BindProperty(minion => minion.Name)
            .BindProperty(minion => minion.Pay)
            .BindProperty(minion => minion.IsActive)
            .BindProperty(minion => minion.CauseForDeparture, "Departure", BindingUse.Projection);
    }

    private static string[] Names(string query, bool ignore = true)
    {
        return [.. Bound(ignore).ApplyCondition(query).Build().ToList().Select(minion => minion.Name).Order()];
    }

    // ---------- where the answer lives ----------

    /// <summary>
    /// It is a setting, so it can be decided where the rest of them are and never mentioned again
    /// </summary>
    [Fact]
    public void ItCanBeSetWithTheOtherSettings()
    {
        var lenient = MinionTestData.Minions()
            .WithWeequery(InquirySettings.Default with { IgnoreUnboundFields = true })
            .BindProperty(minion => minion.Name)
            .ApplyCondition("Gizmo = 3");

        Assert.Equal(4, lenient.Build().ToList().Count);
        Assert.Single(lenient.DroppedFields);
    }

    /// <summary>
    /// It travels with the Inquiry, so every copy an apply makes carries it and no apply has to know about it
    /// </summary>
    [Fact]
    public void ItSurvivesEveryApply()
    {
        var inquiry = MinionTestData.Minions()
            .WithWeequery(InquirySettings.Default with { IgnoreUnboundFields = true })
            .BindProperty(minion => minion.Name)
            .ApplyCondition("Name StartsWith 'A'")
            .ApplySorts("Name")
            .ApplyPagination(pageSize: 2, page: 0);

        Assert.True(inquiry.Settings.IgnoreUnboundFields);
    }

    /// <summary>
    /// Every setting stands on its own, so asking for this one says nothing about the rest
    /// </summary>
    [Fact]
    public void ItSitsBesideTheOtherSettings()
    {
        var settings = InquirySettings.Default with
        {
            StringComparison = StringComparison.OrdinalIgnoreCase,
            DefaultPageSize = 25,
            Operators = OperatorSupport.Without(Operator.IsMatch),
            IgnoreUnboundFields = true,
        };

        var inquiry = MinionTestData.Minions().WithWeequery(settings).BindProperty(minion => minion.Name);

        Assert.True(inquiry.Settings.IgnoreUnboundFields);
        Assert.Equal(StringComparison.OrdinalIgnoreCase, inquiry.Settings.StringComparison);
        Assert.Equal(25, inquiry.Settings.DefaultPageSize);
        Assert.False(inquiry.Settings.Operators.Allows(Operator.IsMatch));
    }

    // ---------- off by default ----------

    [Fact]
    public void ItIsOffUntilAskedFor()
    {
        var inquiry = MinionTestData.Minions().WithWeequery().BindProperty(minion => minion.Name);

        Assert.Throws<WeequeryException>(() => inquiry.ApplyCondition("Gizmo = 3").Build().ToList());
    }

    [Fact]
    public void AndCanBeTurnedBackOff()
    {
        Assert.Throws<WeequeryException>(() => Names("Gizmo = 3", ignore: false));
    }

    // ---------- conditions ----------

    [Fact]
    public void AnUnboundTestIsDroppedFromAConjunction()
    {
        // Three are active, and the Gizmo half is forgotten rather than refused
        Assert.Equal(["Alice Fox", "Bob Samuelson", "David Edgars"], Names("IsActive = true AND Gizmo = 3"));
    }

    /// <summary>Dropping widens, so an OR loses an alternative and keeps the rest</summary>
    [Fact]
    public void AnUnboundTestIsDroppedFromADisjunction()
    {
        Assert.Equal(["Charlie Smith"], Names("Name = 'Charlie Smith' OR Gizmo = 3"));
    }

    [Fact]
    public void ItReachesEveryLevelOfNesting()
    {
        Assert.Equal(["Alice Fox"], Names("Name = 'Alice Fox' AND (Pay > 1 OR (Gizmo = 3 AND Widget IsNull))"));
    }

    /// <summary>The negation of nothing is nothing, rather than everything</summary>
    [Fact]
    public void ANegationOfADroppedTestGoesWithIt()
    {
        Assert.Equal(["Charlie Smith"], Names("Name = 'Charlie Smith' AND NOT (Gizmo = 3)"));
    }

    /// <summary>
    /// The hazard, stated as a test so it cannot quietly stop being true: a condition that is entirely unbound
    /// prunes to nothing, and a query with no condition returns every row.
    /// </summary>
    [Fact]
    public void AConditionThatIsEntirelyUnboundStopsFilteringAltogether()
    {
        Assert.Equal(4, Names("Gizmo = 3").Length);
        Assert.Equal(4, Names("Gizmo = 3 OR Widget = 4").Length);
        Assert.Equal(4, Names("NOT (Gizmo = 3)").Length);
    }

    /// <summary>An operand naming a property is a read of it, so one nobody bound takes the comparison with it</summary>
    [Fact]
    public void AComparisonAgainstAnUnboundPropertyIsDropped()
    {
        Assert.Equal(["Alice Fox", "Bob Samuelson", "David Edgars"], Names("IsActive = true AND Pay > [Gizmo]"));
    }

    [Fact]
    public void APackedConditionIsPrunedToo()
    {
        var packed = ConditionFunctions.ParseQuery("IsActive = true AND Gizmo = 3", QueryStyle.Native)!.Pack();

        var names = Bound(ignore: true).ApplyCondition(packed).Build().ToList().Select(minion => minion.Name);

        Assert.Equal(["Alice Fox", "Bob Samuelson", "David Edgars"], names.Order());
    }

    /// <summary>Two applied conditions are ANDed, and pruning happens after they are put together</summary>
    [Fact]
    public void SeveralAppliedConditionsArePrunedAsOne()
    {
        var names = Bound(ignore: true)
            .ApplyCondition("IsActive = true")
            .ApplyCondition("Gizmo = 3")
            .Build()
            .ToList()
            .Select(minion => minion.Name);

        Assert.Equal(["Alice Fox", "Bob Samuelson", "David Edgars"], names.Order());
    }

    // ---------- sorts ----------

    [Fact]
    public void AnUnboundSortIsDroppedAndTheRestStillApply()
    {
        var names = Bound(ignore: true)
            .ApplySorts([new Sort("Gizmo", SortDirection.Ascending), new Sort("Name", SortDirection.Descending)])
            .Build()
            .ToList()
            .Select(minion => minion.Name);

        Assert.Equal(["David Edgars", "Charlie Smith", "Bob Samuelson", "Alice Fox"], names);
    }

    [Fact]
    public void EverySortDroppingLeavesTheQueryUnordered()
    {
        var query = Bound(ignore: true).ApplySorts([new Sort("Gizmo", SortDirection.Ascending)]).Build();

        Assert.Equal(4, query.Count());
    }

    // ---------- projections ----------

    [Fact]
    public void AnUnboundProjectedFieldIsDropped()
    {
        var row = Bound(ignore: true).ApplyProjection("Name, Gizmo, Pay").BuildProjected().First();

        Assert.Equal(["Name", "Pay"], row.Keys);
    }

    /// <summary>
    /// A caller who asked only for columns that are gone asked for some columns and can have none of them, which
    /// is not the same as having asked for all of them
    /// </summary>
    [Fact]
    public void AProjectionWhoseEveryFieldWentIsARowOfNoColumns()
    {
        var rows = Bound(ignore: true).ApplyProjection("Gizmo, Widget").BuildProjected().ToList();

        Assert.Equal(4, rows.Count);
        Assert.Empty(rows[0]);
    }

    [Fact]
    public void NoProjectionStillReadsEverythingReadable()
    {
        Assert.Equal(["Departure", "IsActive", "Name", "Pay"], Bound(ignore: true).BuildProjected().First().Keys.Order(), StringComparer.Ordinal);
    }

    // ---------- what it does not excuse ----------

    /// <summary>
    /// A binding that does not grant the use is a deliberate statement about what a caller may do, and quietly
    /// ignoring one would undo the point of making it
    /// </summary>
    [Fact]
    public void ANarrowedBindingIsStillRefused()
    {
        var condition = Assert.Throws<WeequeryException>(() => Names("Departure IsNull"));
        Assert.Contains(nameof(BindingUse.Projection), condition.Message);

        Assert.Throws<WeequeryException>(() => Bound(ignore: true)
            .ApplySorts([new Sort("Departure", SortDirection.Ascending)])
            .Build()
            .ToList());
    }

    [Fact]
    public void EverythingElseWrongWithAQueryIsStillWrong()
    {
        // Malformed text
        Assert.Throws<WeequeryException>(() => Names("Name ="));

        // An operator that does not fit the property
        Assert.Throws<WeequeryException>(() => Names("Pay StartsWith 'x'"));

        // A value that will not parse
        Assert.Throws<WeequeryException>(() => Names("Pay > 'not a number'"));
    }

    // ---------- collections ----------

    private static IQueryable<Crew> Crews()
    {
        return new List<Crew>
        {
            new() { Id = 1, Name = "Alpha", Heists = [new() { Target = "Bank", Take = 500 }] },
            new() { Id = 2, Name = "Beta", Heists = [] },
        }.AsQueryable();
    }

    private static Inquiry<Crew> BoundCrews()
    {
        return Crews()
            .WithWeequery(InquirySettings.Default with { IgnoreUnboundFields = true })
            .BindProperty(crew => crew.Name)
            .BindCollection(crew => crew.Heists, "Heists", inner => inner.BindProperty(heist => heist.Take));
    }

    /// <summary>The inside is its own allow-list, so it is that one deciding what survives in there</summary>
    [Fact]
    public void AnUnboundFieldInsideAQuantifierIsDropped()
    {
        var ids = BoundCrews().ApplyCondition("Heists Any (Take > 100 AND Loot = 3)").Build().ToList().Select(crew => crew.Id);

        Assert.Equal([1], ids);
    }

    /// <summary>A quantifier left with no test is a question that was not asked, so it goes too</summary>
    [Fact]
    public void AQuantifierWithNothingLeftInsideIsDropped()
    {
        Assert.Equal(2, BoundCrews().ApplyCondition("Heists Any (Loot = 3)").Build().Count());
        Assert.Equal([1], BoundCrews().ApplyCondition("Name = 'Alpha' AND Heists Any (Loot = 3)").Build().ToList().Select(crew => crew.Id));
    }

    [Fact]
    public void AQuantifierOverAnUnboundCollectionIsDropped()
    {
        Assert.Equal([1], BoundCrews().ApplyCondition("Name = 'Alpha' AND Jobs Any (Take > 1)").Build().ToList().Select(crew => crew.Id));
    }

    /// <summary>And the outer allow-list is still not in scope inside, ignoring or not</summary>
    [Fact]
    public void TheTwoAllowListsStillDoNotLeak()
    {
        // Name is bound on the entity and not inside, so inside it is unbound and drops
        Assert.Equal(2, BoundCrews().ApplyCondition("Heists Any (Name = 'Alpha')").Build().Count());
    }
}

/// <summary>
/// What the last build took out, so a caller can tell whoever wrote the query which parts of it no longer apply.
/// <para>
/// The other half of <see cref="IgnoreUnboundFieldsTests"/>: dropping quietly is only useful if the application
/// can stop being quiet about it when it wants to.
/// </para>
/// </summary>
public class DroppedFieldsTests
{
    private static Inquiry<Minion> Bound()
    {
        return MinionTestData.Minions()
            .WithWeequery(InquirySettings.Default with { IgnoreUnboundFields = true })
            .BindProperty(minion => minion.Name)
            .BindProperty(minion => minion.Pay)
            .BindProperty(minion => minion.IsActive);
    }

    [Fact]
    public void NothingIsDroppedWhenNothingIsMissing()
    {
        var inquiry = Bound().ApplyCondition("IsActive = true");

        inquiry.Build().ToList();

        Assert.Empty(inquiry.DroppedFields);
    }

    /// <summary>Nothing is dropped when dropping was never asked for, since it would have been refused instead</summary>
    [Fact]
    public void NothingIsDroppedWhenDroppingIsOff()
    {
        var inquiry = MinionTestData.Minions()
            .WithWeequery()
            .BindProperty(minion => minion.Name)
            .ApplyCondition("Gizmo = 3");

        Assert.Throws<WeequeryException>(() => inquiry.Build().ToList());
        Assert.Empty(inquiry.DroppedFields);
    }

    [Fact]
    public void AConditionSaysWhatItLost()
    {
        var inquiry = Bound().ApplyCondition("IsActive = true AND Gizmo = 3");

        inquiry.Build().ToList();

        var dropped = Assert.Single(inquiry.DroppedFields);

        Assert.Equal("Gizmo", dropped.Field);
        Assert.Equal(BindingUse.Test, dropped.From);
    }

    [Fact]
    public void ASortAndAProjectionSayTooAndSayWhichTheyAre()
    {
        var inquiry = Bound()
            .ApplySorts([new Sort("Gizmo", SortDirection.Ascending)])
            .ApplyProjection("Name, Widget");

        inquiry.BuildProjected().ToList();

        Assert.Equal(
            [new DroppedField("Gizmo", BindingUse.Sort), new DroppedField("Widget", BindingUse.Projection)],
            inquiry.DroppedFields);
    }

    /// <summary>Two parts of a query losing the same key is two things worth saying</summary>
    [Fact]
    public void OneKeyMissingFromTwoPlacesIsReportedTwice()
    {
        var inquiry = Bound()
            .ApplyCondition("Gizmo = 3")
            .ApplySorts([new Sort("Gizmo", SortDirection.Ascending)]);

        inquiry.Build().ToList();

        Assert.Equal([BindingUse.Test, BindingUse.Sort], inquiry.DroppedFields.Select(entry => entry.From));
    }

    /// <summary>One part of a query losing it twice is one thing, said once</summary>
    [Fact]
    public void OneKeyMissingTwiceInOnePlaceIsReportedOnce()
    {
        var inquiry = Bound().ApplyCondition("Gizmo = 3 OR Gizmo = 4 OR gizmo = 5");

        inquiry.Build().ToList();

        Assert.Single(inquiry.DroppedFields);
    }

    /// <summary>The operand is the part nothing bound, so the operand is what gets named</summary>
    [Fact]
    public void AnOperandIsReportedByItsOwnName()
    {
        var inquiry = Bound().ApplyCondition("Pay > [Gizmo]");

        inquiry.Build().ToList();

        Assert.Equal("Gizmo", Assert.Single(inquiry.DroppedFields).Field);
    }

    [Fact]
    public void EachBuildDescribesItsOwnQuery()
    {
        var inquiry = Bound().ApplyCondition("Gizmo = 3");

        inquiry.Build().ToList();
        inquiry.Build().ToList();

        // Reset rather than accumulated, so building twice does not say it twice
        Assert.Single(inquiry.DroppedFields);
    }

    /// <summary>The projector is read twice for a paged projection, and that is still one drop</summary>
    [Fact]
    public void APagedProjectionDoesNotDoubleCount()
    {
        var inquiry = Bound().ApplyProjection("Name, Widget").ApplyPagination(pageSize: 2, page: 0);

        var (page, _) = inquiry.BuildPagedProjected();
        page.ToList();

        Assert.Equal("Widget", Assert.Single(inquiry.DroppedFields).Field);
    }

    /// <summary>Ready when the build returns, rather than when a row is read</summary>
    [Fact]
    public void ItIsFilledInBeforeAnythingIsEnumerated()
    {
        var inquiry = Bound().ApplyCondition("Gizmo = 3");

        var query = inquiry.Build();

        Assert.Single(inquiry.DroppedFields);
        Assert.Equal(4, query.Count());
    }

    /// <summary>Inside a quantifier the name is the element's, which is what the query wrote</summary>
    [Fact]
    public void AFieldInsideAQuantifierIsNamedAsTheQueryWroteIt()
    {
        var inquiry = new List<Crew>
        {
            new() { Id = 1, Name = "Alpha", Heists = [new() { Target = "Bank", Take = 500 }] },
        }.AsQueryable()
            .WithWeequery(InquirySettings.Default with { IgnoreUnboundFields = true })
            .BindProperty(crew => crew.Name)
            .BindCollection(crew => crew.Heists, "Heists", inner => inner.BindProperty(heist => heist.Take))
            .ApplyCondition("Heists Any (Take > 1 AND Loot = 3)");

        inquiry.Build().ToList();

        Assert.Equal("Loot", Assert.Single(inquiry.DroppedFields).Field);
    }

    [Fact]
    public void AnUnboundCollectionIsReportedByItsOwnKey()
    {
        var inquiry = new List<Crew>().AsQueryable()
            .WithWeequery(InquirySettings.Default with { IgnoreUnboundFields = true })
            .BindProperty(crew => crew.Name)
            .ApplyCondition("Jobs Any (Take > 1)");

        inquiry.Build().ToList();

        Assert.Equal("Jobs", Assert.Single(inquiry.DroppedFields).Field);
    }

    /// <summary>An index is not the missing thing; the binding it is taken on is</summary>
    [Fact]
    public void AnIndexedFieldIsReportedByItsKey()
    {
        var inquiry = Bound().ApplyProjection("Name, Tallies[apples], Tallies[pears]");

        inquiry.BuildProjected().ToList();

        Assert.Equal("Tallies", Assert.Single(inquiry.DroppedFields).Field);
    }

    [Fact]
    public void ItReadsAsWhatHappened()
    {
        Assert.Equal("'Gizmo' dropped from the test, it does not match a binding", new DroppedField("Gizmo", BindingUse.Test).ToString());
    }
}
