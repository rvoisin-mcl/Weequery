using Tests.Common;
using Weequery;
using Weequery.Interfaces;

namespace Tests.Unit;

/// <summary>
/// A whole query as it arrives from a caller, and the one call that applies it. See <see cref="QueryRequest"/>
/// and <see cref="Inquiry{T}.ApplyRequest"/>.
/// <para>
/// What these pin is that applying a request is exactly the four calls it stands in for, that a member nobody
/// filled in says nothing rather than saying "none", and that the allow-list is untouched by any of it: a request
/// names fields, the bindings decide.
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
        var request = new QueryRequest { Filter = "Pay > 0", Sort = "Pay DESC", PageSize = 2, Page = 0 };

        Assert.Equal(["Charlie", "Alice"], Names(Bound().ApplyRequest(request).Build()));
    }

    [Fact]
    public void ARequestAppliesItsFields()
    {
        var request = new QueryRequest { Filter = "Name StartsWith 'Alice'", Fields = "Name, Pay" };

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
        var request = new QueryRequest { Filter = "Pay > 0", Sort = "Pay DESC", Fields = "Name", PageSize = 2, Page = 1 };

        var (page, total) = Bound().ApplyRequest(request).BuildPagedProjected();

        Assert.Equal(3, total.FirstOrDefault());
        Assert.Equal("David Edgars", Assert.Single(page.ToList())["Name"]);
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

    [Fact]
    public void NoSortTakesTheDefault()
    {
        Assert.Equal(["Alice", "Bob", "Charlie", "David"], Names(Bound().ApplyRequest(new QueryRequest(), ByName).Build()));
    }

    [Fact]
    public void ASortTheRequestNamedBeatsTheDefault()
    {
        var request = new QueryRequest { Sort = "Pay DESC" };

        Assert.Equal(["Charlie", "Alice", "David", "Bob"], Names(Bound().ApplyRequest(request, ByName).Build()));
    }

    [Fact]
    public void ASizeWithNoPageIsTheFirstPage()
    {
        var request = new QueryRequest { Sort = "Pay DESC", PageSize = 2 };

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

        Assert.Equal(["Charlie", "Alice"], Names(inquiry.ApplyRequest(new QueryRequest { Sort = "Pay DESC" }).Build()));
    }

    /// <summary>
    /// The odd one out, and deliberately: a projection is one list rather than something that accumulates, so a
    /// request naming no fields is the caller asking for all of the ones they may have
    /// </summary>
    [Fact]
    public void NoFieldsClearsAProjectionAlreadyApplied()
    {
        var inquiry = Bound().ApplyProjection("Name").ApplyRequest(new QueryRequest { Filter = "Name StartsWith 'Alice'" });

        Assert.True(inquiry.AppliedProjection.IsEmpty);
    }

    // ---------- both forms of condition ----------

    [Fact]
    public void APackedConditionIsUnpackedAsAFilterStringWouldBe()
    {
        ICondition condition = new OneValueCondition<decimal>(Operator.GreaterThan, "Pay", 10000m);

        var request = new QueryRequest { Condition = (PackedCondition?)condition.Pack(), Sort = "Pay DESC" };

        Assert.Equal(["Charlie", "Alice"], Names(Bound().ApplyRequest(request).Build()));
    }

    /// <summary>
    /// The packed form needs no parsing and cannot be spelled two ways, so it is the one that wins
    /// </summary>
    [Fact]
    public void APackedConditionBeatsAFilterString()
    {
        ICondition condition = new OneValueCondition<decimal>(Operator.GreaterThan, "Pay", 10000m);

        var request = new QueryRequest { Condition = (PackedCondition?)condition.Pack(), Filter = "Pay = 0" };

        Assert.Equal(2, Bound().ApplyRequest(request).Build().Count());
    }

    // ---------- it grants nothing ----------

    [Fact]
    public void ARequestNamingAnUnboundFieldIsRefusedAsAnyConditionIs()
    {
        var inquiry = Bound().ApplyRequest(new QueryRequest { Filter = "Gizmo = 3" });

        Assert.Throws<WeequeryException>(() => inquiry.Build());
    }

    [Fact]
    public void AMalformedHalfThrowsWhereItIsApplied()
    {
        Assert.Throws<WeequeryException>(() => Bound().ApplyRequest(new QueryRequest { Filter = "Pay >" }));
        Assert.Throws<WeequeryException>(() => Bound().ApplyRequest(new QueryRequest { Sort = "Pay SIDEWAYS" }));
        Assert.Throws<WeequeryException>(() => Bound().ApplyRequest(new QueryRequest { PageSize = 1000, Page = int.MaxValue }));
    }

    /// <summary>
    /// The other field a caller gets wrong, folded the same way: there is nothing behind the first page to ask
    /// for, so asking for it gets the first page
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void APageBehindTheFirstOneIsTheFirstOne(int? page)
    {
        var request = new QueryRequest { Sort = "Pay DESC", PageSize = 2, Page = page };

        Assert.Equal(["Charlie", "Alice"], Names(Bound().ApplyRequest(request).Build()));
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

        var request = new QueryRequest { Sort = "Pay DESC", PageSize = size };

        Assert.Equal(["Charlie", "Alice"], Names(inquiry.ApplyRequest(request).Build()));
    }

    [Fact]
    public void TheStyleReachesTheTextHalves()
    {
        Assert.Throws<WeequeryException>(() => Bound().ApplyRequest(new QueryRequest { Filter = "Pay > 0 && IsActive = true" }));

        Assert.Equal(2, Bound().ApplyRequest(new QueryRequest { Filter = "Pay > 0 && IsActive = true" }, style: QueryStyle.CSharp).Build().Count());
    }

    // ---------- validating one ----------

    /// <summary>
    /// The reason the request overload exists: text that will not parse never reaches Validate() at all, because
    /// applying it throws first
    /// </summary>
    [Fact]
    public void ValidatingARequestReportsWhatWillNotParseRatherThanThrowing()
    {
        var result = Bound().Validate(new QueryRequest { Filter = "Pay >" });

        Assert.False(result.IsValid);
        Assert.Equal(BindingUse.Test, Assert.Single(result.Problems).Part);
    }

    [Fact]
    public void ValidatingARequestReportsEveryHalfAtOnce()
    {
        var request = new QueryRequest { Filter = "Gizmo = 3", Sort = "Doohickey", Fields = "Widget", PageSize = 1000, Page = int.MaxValue };

        var parts = Bound().Validate(request).Problems.Select(problem => problem.Part).ToList();

        Assert.Contains(BindingUse.Test, parts);
        Assert.Contains(BindingUse.Sort, parts);
        Assert.Contains(BindingUse.Projection, parts);
        Assert.Contains(BindingUse.None, parts);
    }

    [Fact]
    public void AGoodRequestValidates()
    {
        var request = new QueryRequest { Filter = "Pay > 0", Sort = "Pay DESC", Fields = "Name", PageSize = 2, Page = 0 };

        Assert.True(Bound().Validate(request).IsValid);
    }

    /// <summary>
    /// Asked on a copy, so the Inquiry you go on to build is the one you had
    /// </summary>
    [Fact]
    public void ValidatingARequestDoesNotApplyIt()
    {
        var inquiry = Bound();

        Assert.True(inquiry.Validate(new QueryRequest { Filter = "Pay > 10000" }).IsValid);

        Assert.Equal(4, inquiry.Build().Count());
    }

    /// <summary>
    /// It answers about the request on top of what is already applied, since that is what applying it would do
    /// </summary>
    [Fact]
    public void ValidatingARequestSeesTheConditionsAlreadyApplied()
    {
        var inquiry = Bound().ApplyCondition("Gizmo = 3");

        Assert.False(inquiry.Validate(new QueryRequest { Filter = "Pay > 0" }).IsValid);
    }

    // ---------- reading the halves on their own ----------

    [Fact]
    public void TheHalvesUnpackIndependently()
    {
        var request = new QueryRequest { Filter = "Pay > 0", Sort = "Pay DESC", Fields = "Name, Pay" };

        Assert.NotNull(request.UnpackCondition());
        Assert.Equal("Pay", Assert.Single(request.UnpackSorts()).Field);
        Assert.Equal(["Name", "Pay"], request.UnpackProjection().Fields);
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
