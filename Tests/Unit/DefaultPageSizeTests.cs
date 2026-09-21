using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// The page size a query takes where the caller named none, see <see cref="InquirySettings.DefaultPageSize"/>.
/// <para>
/// The behaviour worth pinning is the one that inverts: with a default set, a query that never mentioned paging
/// is windowed anyway. That is the whole point of it — a caller cannot ask for the whole table by leaving a field
/// out — and it is also the thing that would be a surprise if it were on without being asked for, which is why
/// the default default is no default at all.
/// </para>
/// </summary>
public class DefaultPageSizeTests
{
    private static readonly InquirySettings PagesByTwo = InquirySettings.Default with { DefaultPageSize = 2 };

    private static string[] Names(IQueryable<Minion> query)
    {
        return [.. query.ToList().Select(minion => minion.Name.Split(' ')[0])];
    }

    private static Inquiry<Minion> Sorted(InquirySettings? settings = null)
    {
        return MinionTestData.Minions()
            .WithWeequery(settings)
            .BindProperties(Minion.Bindings)
            .ApplySorts("Pay DESC");
    }

    // ---------- off, which is how it arrives ----------

    [Fact]
    public void ThereIsNoDefaultUnlessOneIsAskedFor()
    {
        Assert.Null(InquirySettings.Default.DefaultPageSize);
        Assert.Null(new InquirySettings().DefaultPageSize);
    }

    [Fact]
    public void WithNoDefaultAQueryThatNamesNoPagingReadsEverything()
    {
        Assert.Equal(["Charlie", "Alice", "David", "Bob"], Names(Sorted().Build()));
    }

    // ---------- on ----------

    /// <summary>
    /// The case it exists for: nothing about this query mentions paging, and it is paged
    /// </summary>
    [Fact]
    public void ADefaultWindowsAQueryThatNeverAskedToBePaged()
    {
        Assert.Equal(["Charlie", "Alice"], Names(Sorted(PagesByTwo).Build()));
    }

    /// <summary>
    /// A caller naming a page and leaving the size to the server, which is the other half of the same idea
    /// </summary>
    [Fact]
    public void ANullSizeTakesTheDefaultAndKeepsThePageAsked()
    {
        Assert.Equal(["David", "Bob"], Names(Sorted(PagesByTwo).ApplyPagination(null, 1).Build()));
    }

    [Fact]
    public void ASizeTheCallerNamedWins()
    {
        Assert.Equal(["Charlie", "Alice", "David"], Names(Sorted(PagesByTwo).ApplyPagination(3, 0).Build()));
    }

    [Fact]
    public void TheDefaultReachesThePagedBuildsToo()
    {
        var (page, matches) = Sorted(PagesByTwo).BuildPaged();

        Assert.Equal(["Charlie", "Alice"], Names(page));
        Assert.Equal(4, matches.Count());
    }

    [Fact]
    public void TheDefaultReachesAProjectedBuild()
    {
        var rows = Sorted(PagesByTwo).ApplyProjection("Name").BuildProjected().ToList();

        Assert.Equal(2, rows.Count);
    }

    // ---------- carried, as settings are ----------

    [Fact]
    public void ACloneKeepsTheSettingsAndTheSizeAlreadyNamed()
    {
        var clone = Sorted(PagesByTwo).ApplyPagination(3, 0).Clone();

        Assert.Equal(2, clone.Settings.DefaultPageSize);
        Assert.Equal(["Charlie", "Alice", "David"], Names(clone.Build()));
    }

    // ---------- what it refuses ----------

    /// <summary>
    /// Both ways of arriving at one, since a record built positionally and a record copied with a <c>with</c>
    /// reach the property by different routes
    /// </summary>
    [Fact]
    public void ADefaultCanBeGivenPositionallyOrByCopy()
    {
        Assert.Equal(2, new InquirySettings(StringComparison.Ordinal, 2).DefaultPageSize);
        Assert.Equal(2, (InquirySettings.Default with { DefaultPageSize = 2 }).DefaultPageSize);
        Assert.Null((PagesByTwo with { DefaultPageSize = null }).DefaultPageSize);
    }

    /// <summary>
    /// The settings end of it, which is refused: a default is written once in your own startup, where a zero is
    /// a typo rather than a caller's accident. And the check holds whichever of the two routes above was taken
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ADefaultThatIsNotGreaterThanZeroIsRefusedWhereItIsWritten(int size)
    {
        var copied = Assert.Throws<WeequeryException>(() => InquirySettings.Default with { DefaultPageSize = size });
        var built = Assert.Throws<WeequeryException>(() => new InquirySettings(StringComparison.Ordinal, size));

        Assert.Equal(WeequeryError.ArgumentInvalid, copied.Error);
        Assert.Equal(WeequeryError.ArgumentInvalid, built.Error);
    }

    /// <summary>
    /// The caller's end of it, which is folded rather than refused: a size that could not hold a page is no size
    /// at all, and null, zero and a negative all arrive at the default
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void ASizeThatCouldNotHoldAPageTakesTheDefault(int? size)
    {
        Assert.Equal(["Charlie", "Alice"], Names(Sorted(PagesByTwo).ApplyPagination(size, 0).Build()));
        Assert.Equal(["David", "Bob"], Names(Sorted(PagesByTwo).ApplyPagination(size, 1).Build()));
    }

    /// <summary>
    /// And with no default to fall back to it is no window, rather than a page of nothing
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void ASizeThatCouldNotHoldAPageAndNoDefaultIsNoWindow(int? size)
    {
        Assert.Equal(["Charlie", "Alice", "David", "Bob"], Names(Sorted().ApplyPagination(size, 0).Build()));
    }

    /// <summary>
    /// A page behind the first one is the first one, whether the size came from the caller or from the settings
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void ANegativePageIsTheFirstPage(int page)
    {
        Assert.Equal(["Charlie", "Alice"], Names(Sorted(PagesByTwo).ApplyPagination(2, page).Build()));
        Assert.Equal(["Charlie", "Alice"], Names(Sorted(PagesByTwo).ApplyPagination(null, page).Build()));
    }

    /// <summary>
    /// With neither a size nor a default there is no window, so a nonsense page changes nothing at all
    /// </summary>
    [Fact]
    public void ANegativePageWithNoWindowIsStillEveryRow()
    {
        Assert.Equal(["Charlie", "Alice", "David", "Bob"], Names(Sorted().ApplyPagination(null, -1).Build()));
    }

    /// <summary>
    /// The pair is only settled when the query is built where the size came from the settings, so the overflow
    /// check has to happen there as well as at the call
    /// </summary>
    [Fact]
    public void ADefaultSizeAndAFarPageThatOverflowAreRefusedWhenBuilt()
    {
        var inquiry = Sorted(InquirySettings.Default with { DefaultPageSize = 1000 }).ApplyPagination(null, int.MaxValue);

        var error = Assert.Throws<WeequeryException>(() => inquiry.Build());

        Assert.Equal(WeequeryError.ArgumentInvalid, error.Error);
    }
}
