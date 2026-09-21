using Weequery;

namespace Tests.Unit;

public class Crate
{
    public int Weight { get; set; }
    public string Label { get; set; } = "";
}

public class Depot
{
    public int Id { get; set; }
    public List<Crate>? Crates { get; set; }
    public Dictionary<string, int>? Tallies { get; set; }
    public int Threshold { get; set; }
}

/// <summary>
/// The three other places an index can appear, once a condition can carry one.
/// <list type="number">
/// <item><description>
/// In a <b>binding path</b>, where it is fixed by the code that bound it and the path carries on past it:
/// <c>BindProperty("Crates[0].Weight", "FirstWeight")</c>. This is what TODO.txt described.
/// </description></item>
/// <item><description>
/// In an <b>operand</b>, comparing a property against an element of a collection: <c>Threshold &lt; [Tallies][apples]</c>.
/// </description></item>
/// <item><description>
/// In a <b>sort</b>: <c>OrderBy Tallies[apples] DESC</c>.
/// </description></item>
/// </list>
/// <para>
/// The condition form keeps the index beside the field, having somewhere to put it. An operand and a sort have
/// nowhere, so they carry it in the field text and it is taken apart again on lookup. All four end up at the same
/// guarded element access, so a missing element is a null in every one of them.
/// </para>
/// </summary>
public class CollectionIndexPositionTests
{
    private static IQueryable<Depot> Depots()
    {
        return new List<Depot>
        {
            new() { Id = 1, Crates = [new() { Weight = 50, Label = "heavy" }, new() { Weight = 5 }], Tallies = new() { ["apples"] = 7 }, Threshold = 6 },
            new() { Id = 2, Crates = [new() { Weight = 1, Label = "light" }], Tallies = new() { ["apples"] = 2 }, Threshold = 6 },
            new() { Id = 3, Crates = null, Tallies = null, Threshold = 6 },
        }.AsQueryable();
    }

    /// <summary>Indices fixed by the code that bound them</summary>
    private static int[] ByPath(string query)
    {
        return [.. Depots().WithWeequery()
            .BindProperty(depot => depot.Id)
            .BindProperty("Crates[0].Weight", "FirstWeight")
            .BindProperty("Crates[0].Label", "FirstLabel")
            .BindProperty("Tallies[apples]", "Apples")
            .ApplyCondition(query)
            .Build().ToList().Select(depot => depot.Id)];
    }

    /// <summary>Collections bound whole, indices chosen by the query</summary>
    private static int[] ByQuery(string query, string? sort = null)
    {
        var inquiry = Depots().WithWeequery()
            .BindProperty(depot => depot.Id)
            .BindProperty(depot => depot.Tallies)
            .BindProperty(depot => depot.Threshold)
            .ApplyCondition(query);

        if (sort is not null) { inquiry = inquiry.ApplySorts(sort); }

        return [.. inquiry.Build().ToList().Select(depot => depot.Id)];
    }

    // ---------- the path form ----------

    /// <summary>
    /// The one TODO.txt asked for: index a collection and keep going, so the element's own properties are
    /// reachable. The index is the application's choice here, not the caller's.
    /// </summary>
    [Fact]
    public void APathCanIndexAndThenCarryOn()
    {
        Assert.Equal([1], ByPath("FirstWeight > 10"));
        Assert.Equal([1], ByPath("FirstLabel = 'heavy'"));
    }

    [Fact]
    public void APathCanEndAtTheElement()
    {
        Assert.Equal([1], ByPath("Apples > 5"));
        Assert.Equal([1, 2], ByPath("Apples > 0"));
    }

    /// <summary>
    /// Guarded the same way as everywhere else: depot 3 has no list at all, so the element and everything under
    /// it has no value rather than throwing
    /// </summary>
    [Fact]
    public void AMissingElementInAPathIsStillANull()
    {
        Assert.Equal([3], ByPath("FirstWeight IsNull"));
        Assert.Equal([1, 2], ByPath("FirstWeight IsNotNull"));

        // and the negative operator does not catch it, as it does not catch any other null
        Assert.Equal([1, 2], ByPath("FirstWeight <> 999"));
    }

    /// <summary>
    /// A path is code rather than caller input, so what is refused is a mistake in the calling program
    /// </summary>
    [Theory]
    [InlineData("Crates[0")]        // never closed
    [InlineData("Crates[]")]        // no index
    [InlineData("Crates[0]extra")]  // text after the index
    [InlineData("[0].Weight")]      // no property to index
    public void AMalformedPathIsRefused(string path)
    {
        Assert.Throws<WeequeryException>(() => Depots().WithWeequery().BindProperty(path, "Key"));
    }

    [Fact]
    public void IndexingSomethingThatIsNotACollectionInAPathIsRefused()
    {
        var error = Assert.Throws<WeequeryException>(() => Depots().WithWeequery().BindProperty("Threshold[0]", "Key"));

        Assert.Equal(WeequeryError.PathInvalid, error.Error);
    }

    // ---------- the operand position ----------

    /// <summary>
    /// An indexed field on the left compared against an ordinary bound property on the right
    /// </summary>
    [Fact]
    public void AnIndexedFieldComparesAgainstAnotherProperty()
    {
        Assert.Equal([1], ByQuery("Tallies[apples] > [Threshold]"));
    }

    /// <summary>
    /// And the other way round: the operand is what carries the index
    /// </summary>
    [Fact]
    public void AnOperandCanBeAnIndexedElement()
    {
        Assert.Equal([1], ByQuery("Threshold < [Tallies][apples]"));
    }

    [Fact]
    public void AnIndexedOperandIsWrittenAsTwoBracketPairs()
    {
        var written = ConditionFunctions.ParseQuery("Threshold < [Tallies][apples]")!.ToQuery();

        // "[Tallies[apples]]" is the shape that would not read back, so it is not the shape written
        Assert.Equal("([Threshold] < [Tallies][apples])", written);
        Assert.Equal(written, ConditionFunctions.ParseQuery(written)!.ToQuery());
    }

    /// <summary>
    /// A missing element on the right is a null, so the comparison has nothing to be true about
    /// </summary>
    [Fact]
    public void AnOperandThatIsMissingMatchesNothing()
    {
        Assert.Empty(ByQuery("Threshold < [Tallies][pears]"));
    }

    // ---------- the sort position ----------

    [Fact]
    public void ASortCanNameAnIndexedElement()
    {
        // 7, then 2, then the depot with no dictionary at all
        Assert.Equal([1, 2, 3], ByQuery("Id > 0", "Tallies[apples] DESC"));

        // nulls first ascending, which is what a nullable sorts as
        Assert.Equal([3, 2, 1], ByQuery("Id > 0", "Tallies[apples] ASC"));
    }

    [Fact]
    public void AnIndexedSortRoundTrips()
    {
        var once = Sort.Parse("Tallies[apples] DESC", null).ToQuery();

        Assert.Equal("[Tallies][apples] DESC", once);
        Assert.Equal(once, Sort.Parse(once, null).ToQuery());
    }

    [Fact]
    public void AnIndexedSortReadsInACombinedString()
    {
        var parsed = ParsedQuery.Parse("Id > 0 OrderBy Tallies[apples] DESC");

        Assert.NotNull(parsed.Condition);
        Assert.Equal("Tallies[apples]", Assert.Single(parsed.Sorts).Field);
    }

    [Fact]
    public void SortingOnSomethingThatCannotBeIndexedIsRefused()
    {
        Assert.Throws<WeequeryException>(() => ByQuery("Id > 0", "Threshold[0] DESC"));
    }

    [Fact]
    public void SortingOnAnUnboundIndexedFieldIsStillUnbound()
    {
        var error = Assert.Throws<WeequeryException>(() => ByQuery("Id > 0", "Nothing[0] DESC"));

        Assert.Equal(WeequeryError.UnboundField, error.Error);
    }

    // ---------- the four positions agree ----------

    /// <summary>
    /// The same question asked four ways gives the same rows, which is the point of routing all of them through
    /// one guarded element access rather than three
    /// </summary>
    [Fact]
    public void EveryPositionLandsOnTheSameSemantics()
    {
        var viaCondition = ByQuery("Tallies[apples] > 5");
        var viaPath = ByPath("Apples > 5");
        var viaOperand = ByQuery("Tallies[apples] > [Threshold]");

        Assert.Equal([1], viaCondition);
        Assert.Equal(viaCondition, viaPath);
        Assert.Equal(viaCondition, viaOperand);
    }
}
