using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// A whole query as it arrives from a caller, and the one call that applies it. See <see cref="QueryRequest"/>
/// and <see cref="Inquiry{T}.ApplyRequest"/>.
/// <para>
/// What these pin is that applying a request is exactly the calls it stands in for, that a member nobody filled
/// in says nothing rather than saying "none", and that the allow-list is untouched by any of it: a request names
/// fields, the bindings decide.
/// </para>
/// <para>
/// The query itself is one string, in the grammar <see cref="ParsedQuery"/> reads, so there is one text to be
/// wrong about rather than three to keep in step.
/// </para>
/// </summary>
public class QueryRequestTests
{
    private static readonly Sort[] ByName = [new Sort("Name", SortDirection.Ascending)];

    private static Inquiry<Minion> Bound()
    {
        return MinionTestData.Minions().WithWeequery().BindProperties(Minion.Bindings);
    }

    private static string[] Names(IQueryable<Minion> query)
    {
        return [.. query.ToList().Select(minion => minion.Name.Split(' ')[0])];
    }

    // ---------- the whole of it ----------

    [Fact]
    public void ARequestAppliesItsFilterItsSortAndItsWindow()
    {
        var request = new QueryRequest { Query = "Pay > 0 OrderBy Pay DESC", PageSize = 2, Page = 0 };

        Assert.Equal(["Charlie", "Alice"], Names(Bound().ApplyRequest(request).Build()));
    }

    [Fact]
    public void ARequestAppliesItsFields()
    {
        var request = new QueryRequest { Query = "Name StartsWith 'Alice' Select Name, Pay" };

        var row = Assert.Single(Bound().ApplyRequest(request).BuildProjected().ToList());

        Assert.Equal(["Name", "Pay"], row.Keys);
        Assert.Equal("Alice Fox", row["Name"]);
    }

    /// <summary>
    /// The shape it exists for, end to end: the page and the total, off one request
    /// </summary>
    [Fact]
    public void ARequestFeedsAPagedProjectedBuild()
    {
        var request = new QueryRequest { Query = "Pay > 0 OrderBy Pay DESC Select Name", PageSize = 2, Page = 1 };

        var (page, total) = Bound().ApplyRequest(request).BuildPagedProjected();

        Assert.Equal(3, total.FirstOrDefault());
        Assert.Equal("David Edgars", Assert.Single(page.ToList())["Name"]);
    }

    /// <summary>
    /// All three parts and the window, which is every member there is
    /// </summary>
    [Fact]
    public void OneStringCarriesTheWholeQuery()
    {
        var request = new QueryRequest { Query = "Pay > 0 OrderBy Pay DESC Select Name, Pay" };

        var (condition, sorts, fields) = request.Unpack();

        Assert.Equal("([Pay] > '0')", condition!.ToQuery());
        Assert.Equal("Pay", Assert.Single(sorts).Field);
        Assert.Equal(["Name", "Pay"], fields.Fields);
    }

    // ---------- a member nobody filled in ----------

    [Fact]
    public void AnEmptyRequestIsEveryRow()
    {
        Assert.Equal(4, Bound().ApplyRequest(new QueryRequest()).Build().Count());
    }

    [Fact]
    public void ANullRequestIsANop()
    {
        Assert.Equal(4, Bound().ApplyRequest(null).Build().Count());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AQueryThatSaysNothingAsksForNothing(string? query)
    {
        var request = new QueryRequest { Query = query };

        Assert.Null(request.UnpackCondition());
        Assert.Empty(request.UnpackSorts());
        Assert.True(request.UnpackProjection().IsEmpty);
    }

    [Fact]
    public void NoSortTakesTheDefault()
    {
        Assert.Equal(["Alice", "Bob", "Charlie", "David"], Names(Bound().ApplyRequest(new QueryRequest(), ByName).Build()));
    }

    /// <summary>
    /// A query naming a filter and no sorts is still a query naming no sorts, so the default stands in
    /// </summary>
    [Fact]
    public void ADefaultStandsInBesideAFilterAndAProjection()
    {
        var request = new QueryRequest { Query = "Pay > 0 Select Name" };

        Assert.Equal("Name", Assert.Single(request.UnpackSorts(ByName)).Field);
    }

    [Fact]
    public void ASortTheRequestNamedBeatsTheDefault()
    {
        var request = new QueryRequest { Query = "OrderBy Pay DESC" };

        Assert.Equal(["Charlie", "Alice", "David", "Bob"], Names(Bound().ApplyRequest(request, ByName).Build()));
    }

    [Fact]
    public void ASizeWithNoPageIsTheFirstPage()
    {
        var request = new QueryRequest { Query = "OrderBy Pay DESC", PageSize = 2 };

        Assert.Equal(["Charlie", "Alice"], Names(Bound().ApplyRequest(request).Build()));
    }

    /// <summary>
    /// A request naming no size takes the settings, which is where the two features meet
    /// </summary>
    [Fact]
    public void NoSizeTakesTheDefaultPageSize()
    {
        var inquiry = MinionTestData.Minions()
            .WithWeequery(InquirySettings.Default with { DefaultPageSize = 2 })
            .BindProperties(Minion.Bindings);

        Assert.Equal(["Charlie", "Alice"], Names(inquiry.ApplyRequest(new QueryRequest { Query = "OrderBy Pay DESC" }).Build()));
    }

    /// <summary>
    /// The field a caller is most likely to get wrong, and the one that is folded rather than refused: leaving it
    /// out and sending a zero are the same accident and get the same answer
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void ASizeThatCouldNotHoldAPageTakesTheDefaultRatherThanThrowing(int? size)
    {
        var inquiry = MinionTestData.Minions()
            .WithWeequery(InquirySettings.Default with { DefaultPageSize = 2 })
            .BindProperties(Minion.Bindings);

        var request = new QueryRequest { Query = "OrderBy Pay DESC", PageSize = size };

        Assert.Equal(["Charlie", "Alice"], Names(inquiry.ApplyRequest(request).Build()));
    }

    /// <summary>
    /// The odd one out, and deliberately: a projection is one list rather than something that accumulates, so a
    /// request naming no fields is the caller asking for all of the ones they may have
    /// </summary>
    [Fact]
    public void NoFieldsClearsAProjectionAlreadyApplied()
    {
        var inquiry = Bound().ApplyProjection("Name").ApplyRequest(new QueryRequest { Query = "Name StartsWith 'Alice'" });

        Assert.True(inquiry.AppliedProjection.IsEmpty);
    }

    /// <summary>
    /// The window is not in the query language, so it is a member of its own and rides beside the text
    /// </summary>
    [Fact]
    public void TheWindowIsNotPartOfTheQuery()
    {
        var request = new QueryRequest { Query = "Pay > 0 OrderBy Pay DESC", PageSize = 2, Page = 1 };

        Assert.Equal(["David"], Names(Bound().ApplyRequest(request).Build()));
    }

    // ---------- it grants nothing ----------

    [Fact]
    public void ARequestNamingAnUnboundFieldIsRefusedAsAnyConditionIs()
    {
        var inquiry = Bound().ApplyRequest(new QueryRequest { Query = "Gizmo = 3" });

        Assert.Throws<WeequeryException>(() => inquiry.Build());
    }

    [Fact]
    public void AMalformedQueryThrowsWhereItIsApplied()
    {
        Assert.Throws<WeequeryException>(() => Bound().ApplyRequest(new QueryRequest { Query = "Pay >" }));
        Assert.Throws<WeequeryException>(() => Bound().ApplyRequest(new QueryRequest { Query = "OrderBy Pay SIDEWAYS" }));
        Assert.Throws<WeequeryException>(() => Bound().ApplyRequest(new QueryRequest { Query = "Pay > 0 Select" }));
        Assert.Throws<WeequeryException>(() => Bound().ApplyRequest(new QueryRequest { PageSize = 1000, Page = int.MaxValue }));
    }

    // ---------- one grammar, and no way to ask for another ----------

    /// <summary>
    /// Native and only Native. The deprecated spellings exist for text written before there was a settlement,
    /// and text arriving from outside is not that, so each is refused and each refusal names the one to write.
    /// </summary>
    [Theory]
    [InlineData("Pay > 0 && IsActive = true", "AND")]
    [InlineData("Pay > 0 || Pay = 0", "OR")]
    [InlineData("Alias IS NULL", "IsNull")]
    [InlineData("Alias IS NOT NULL", "IsNotNull")]
    [InlineData("Pay > 0 ORDER BY Pay DESC", "ORDER BY")]
    public void OnlyTheNativeSpellingsAreRead(string query, string instead)
    {
        var error = Assert.Throws<WeequeryException>(() => Bound().ApplyRequest(new QueryRequest { Query = query }));

        Assert.Equal(WeequeryError.QuerySyntax, error.Error);
        Assert.Contains(instead, error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Refused by validating as well, which is where a caller hears about it rather than a handler
    /// </summary>
    [Fact]
    public void ADeprecatedSpellingIsReportedRatherThanThrown()
    {
        var result = Bound().Validate(new QueryRequest { Query = "Pay > 0 && IsActive = true" });

        Assert.False(result.IsValid);
        Assert.Equal(WeequeryError.QuerySyntax, Assert.Single(result.Problems).Error);
    }

    /// <summary>
    /// What Native removed is spellings made of separate words, not alternates that are already one word, so
    /// these still read and still mean what they always did
    /// </summary>
    [Fact]
    public void TheOneWordAlternatesStillRead()
    {
        var request = new QueryRequest { Query = "Pay IN (0, 12000) OrderBy Name" };

        Assert.NotNull(request.UnpackCondition());
        Assert.Equal("Name", Assert.Single(request.UnpackSorts()).Field);
    }

    // ---------- validating one ----------

    /// <summary>
    /// The reason the request overload exists: text that will not parse never reaches Validate() at all, because
    /// applying it throws first
    /// </summary>
    [Fact]
    public void ValidatingARequestReportsWhatWillNotParseRatherThanThrowing()
    {
        var result = Bound().Validate(new QueryRequest { Query = "Pay >" });

        Assert.False(result.IsValid);
        Assert.Equal(BindingUse.None, Assert.Single(result.Problems).Part);
    }

    /// <summary>
    /// One string is one text to read, so a fault in it is one problem rather than the same one three times, and
    /// it is about the request rather than any part of it: the separators are what say which part a word belongs
    /// to, and a string that will not parse is a string whose separators are not settled
    /// </summary>
    [Fact]
    public void AMalformedQueryIsOneProblemAgainstTheRequest()
    {
        var request = new QueryRequest { Query = "Pay Bogus 10000 OrderBy Pay" };

        var problem = Assert.Single(Bound().Validate(request).Problems);

        Assert.Equal(BindingUse.None, problem.Part);
        Assert.Equal(WeequeryError.QuerySyntax, problem.Error);
    }

    /// <summary>
    /// A query that parses is another matter: what it names is checked part by part, so a caller is told which
    /// box to look at
    /// </summary>
    [Fact]
    public void AQueryThatParsesIsStillReportedPerPart()
    {
        var request = new QueryRequest { Query = "Gizmo = 3 OrderBy Doohickey Select Widget" };

        var parts = Bound().Validate(request).Problems.Select(problem => problem.Part).ToList();

        Assert.Contains(BindingUse.Test, parts);
        Assert.Contains(BindingUse.Sort, parts);
        Assert.Contains(BindingUse.Projection, parts);
    }

    [Fact]
    public void ValidatingARequestReportsTheWindowToo()
    {
        var request = new QueryRequest { Query = "Gizmo = 3", PageSize = 1000, Page = int.MaxValue };

        var parts = Bound().Validate(request).Problems.Select(problem => problem.Part).ToList();

        Assert.Contains(BindingUse.Test, parts);
        Assert.Contains(BindingUse.None, parts);
    }

    [Fact]
    public void AGoodRequestValidates()
    {
        var request = new QueryRequest { Query = "Pay > 0 OrderBy Pay DESC Select Name", PageSize = 2, Page = 0 };

        Assert.True(Bound().Validate(request).IsValid);
    }

    /// <summary>
    /// Asked on a copy, so the Inquiry you go on to build is the one you had
    /// </summary>
    [Fact]
    public void ValidatingARequestDoesNotApplyIt()
    {
        var inquiry = Bound();

        Assert.True(inquiry.Validate(new QueryRequest { Query = "Pay > 10000" }).IsValid);

        Assert.Equal(4, inquiry.Build().Count());
    }

    /// <summary>
    /// It answers about the request on top of what is already applied, since that is what applying it would do
    /// </summary>
    [Fact]
    public void ValidatingARequestSeesTheConditionsAlreadyApplied()
    {
        var inquiry = Bound().ApplyCondition("Gizmo = 3");

        Assert.False(inquiry.Validate(new QueryRequest { Query = "Pay > 0" }).IsValid);
    }

    // ---------- reading one part on its own ----------

    [Fact]
    public void EachPartUnpacksOnItsOwn()
    {
        var request = new QueryRequest { Query = "Pay > 0 OrderBy Pay DESC Select Name, Pay" };

        Assert.NotNull(request.UnpackCondition());
        Assert.Equal("Pay", Assert.Single(request.UnpackSorts()).Field);
        Assert.Equal(["Name", "Pay"], request.UnpackProjection().Fields);
    }

    /// <summary>
    /// And says the same as reading all three at once, which is what it is a facet of
    /// </summary>
    [Fact]
    public void APartOnItsOwnAgreesWithTheWhole()
    {
        var request = new QueryRequest { Query = "Pay > 0 OrderBy Pay DESC Select Name" };

        var whole = request.Unpack(ByName);

        Assert.Equal(whole.Condition!.ToQuery(), request.UnpackCondition()!.ToQuery());
        Assert.Equal(whole.Sorts, request.UnpackSorts(ByName));
        Assert.Equal(whole.Projection.Fields, request.UnpackProjection().Fields);
    }

    [Fact]
    public void AnEmptyRequestUnpacksToNothingRatherThanThrowing()
    {
        var request = new QueryRequest();

        Assert.Null(request.UnpackCondition());
        Assert.Empty(request.UnpackSorts());
        Assert.True(request.UnpackProjection().IsEmpty);
    }
}
