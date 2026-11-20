using System.Text.Json;
using Weequery;

namespace Tests.Unit;

public enum Shade { Red, Green, Blue }

/// <summary>Collections of values rather than objects, of every kind a builder compares</summary>
public class Bin
{
    public int Id { get; set; }
    public List<string?>? Tags { get; set; }
    public int[] Scores { get; set; } = [];
    public List<int?> Readings { get; set; } = [];
    public List<Shade> Shades { get; set; } = [];
    public List<Guid> Owners { get; set; } = [];
    public List<DateTime> Dates { get; set; } = [];
    public byte[]? Photo { get; set; }
}

/// <summary>
/// Any, All and None over a collection of values, such as a List&lt;string&gt; or an int[], where the element is
/// named as Value because it has no property to name
/// </summary>
public class ValueCollectionQuantifierTests
{
    private static readonly Guid Alice = new(1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1);
    private static readonly Guid Bob = new(2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2);

    private static IQueryable<Bin> Bins()
    {
        return new List<Bin>
        {
            new() { Id = 1, Tags = ["red", "fragile"], Scores = [3, 12], Readings = [1, null], Shades = [Shade.Red], Owners = [Alice], Dates = [new(2024, 1, 1)] },
            new() { Id = 2, Tags = ["blue", null], Scores = [7], Readings = [], Shades = [Shade.Green, Shade.Blue], Owners = [Alice, Bob], Dates = [new(2025, 6, 1)] },
            new() { Id = 3, Tags = null, Scores = [], Readings = [5], Shades = [], Owners = [], Dates = [] },
        }.AsQueryable();
    }

    private static List<int> Ids(Inquiry<Bin> inquiry, string condition)
    {
        return [.. inquiry.ApplyCondition(condition).Build().Select(bin => bin.Id)];
    }

    private static Inquiry<Bin> Bound()
    {
        return Bins().WithWeequery()
            .BindProperty(bin => bin.Id)
            .BindCollection(bin => bin.Tags!, "Tags")
            .BindCollection(bin => bin.Scores, "Scores")
            .BindCollection(bin => bin.Readings, "Readings")
            .BindCollection(bin => bin.Shades, "Shades")
            .BindCollection(bin => bin.Owners, "Owners")
            .BindCollection(bin => bin.Dates, "Dates");
    }

    // ---------- the three quantifiers ----------

    [Fact]
    public void AnyMatchesWhereOneElementDoes()
    {
        Assert.Equal([1], Ids(Bound(), "Tags Any (Value = 'red')"));
    }

    [Fact]
    public void AllMatchesWhereEveryElementDoes_AndAnEmptyOrMissingOneHasNoneToFail()
    {
        Assert.Equal([2, 3], Ids(Bound(), "Scores All (Value > 5)"));
        Assert.Equal([1, 3], Ids(Bound(), "Tags All (Value IsNotNull)"));
    }

    [Fact]
    public void NoneMatchesWhereNoElementDoes()
    {
        Assert.Equal([2, 3], Ids(Bound(), "Tags None (Value = 'red')"));
    }

    /// <summary>
    /// The point of the parentheses: both tests are put to one element, so this asks for a score between the
    /// two, and crate 1, with a 3 and a 12, has none
    /// </summary>
    [Fact]
    public void SeveralTestsArePutToTheSameElement()
    {
        Assert.Equal([2], Ids(Bound(), "Scores Any (Value > 5 AND Value < 10)"));
        Assert.Equal([1, 2], Ids(Bound(), "Scores Any (Value > 5) AND Scores Any (Value < 10)"));
    }

    // ---------- what an element can be ----------

    [Fact]
    public void AStringElementTakesTheStringOperators()
    {
        Assert.Equal([1], Ids(Bound(), "Tags Any (Value StartsWith 'fra')"));
        Assert.Equal([2], Ids(Bound(), "Tags Any (Value IsIn ('blue', 'green'))"));
    }

    [Fact]
    public void ANullElementIsTestedForNullAndSkippedByAComparison()
    {
        Assert.Equal([2], Ids(Bound(), "Tags Any (Value IsNull)"));
        Assert.Equal([1], Ids(Bound(), "Readings Any (Value IsNull)"));
        Assert.Equal([3], Ids(Bound(), "Readings Any (Value > 2)"));
    }

    [Fact]
    public void AnEnumElementIsComparedByName()
    {
        Assert.Equal([2], Ids(Bound(), "Shades Any (Value = 'Blue')"));
    }

    [Fact]
    public void AGuidElementIsComparedAsAGuid()
    {
        Assert.Equal([2], Ids(Bound(), $"Owners Any (Value = '{Bob}')"));
    }

    [Fact]
    public void ADateElementIsComparedAsADate()
    {
        Assert.Equal([2], Ids(Bound(), "Dates Any (Value > 2025-01-01)"));
    }

    /// <summary>A collection that is not there holds nothing, so Any is false of it and All and None are true</summary>
    [Fact]
    public void AMissingCollectionIsAnEmptyOne()
    {
        Assert.DoesNotContain(3, Ids(Bound(), "Tags Any (Value = 'red')"));
        Assert.Contains(3, Ids(Bound(), "Tags None (Value = 'red')"));
    }

    // ---------- what may be named inside ----------

    [Fact]
    public void ValueIsTheOnlyNameInside()
    {
        var thrown = Assert.Throws<WeequeryException>(() => Ids(Bound(), "Tags Any (Length > 3)"));

        Assert.Equal(WeequeryError.UnboundField, thrown.Error);
    }

    [Fact]
    public void ValueIsMatchedWithoutRegardToCase()
    {
        Assert.Equal([1], Ids(Bound(), "Tags Any (value = 'red')"));
    }

    // ---------- binding it ----------

    [Fact]
    public void TheConfiguringFormBindsItWithBindValue()
    {
        var inquiry = Bins().WithWeequery().BindCollection(bin => bin.Scores, "Scores", inner => inner.BindValue());

        Assert.Equal([2], Ids(inquiry, "Scores Any (Value = 7)"));
    }

    [Fact]
    public void BindValueIsRefusedOnACollectionOfObjects()
    {
        var thrown = Assert.Throws<WeequeryException>(() =>
            new List<Crew>().AsQueryable().WithWeequery().BindCollection(crew => crew.Heists!, "Heists", inner => inner.BindValue()));

        Assert.Equal(WeequeryError.BindingInvalid, thrown.Error);
        Assert.Contains("Heist", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BindResolveBindsEveryCollectionOfValues()
    {
        var resolved = Bins().WithWeequery().BindResolve();

        Assert.Equal([1], Ids(resolved, "Tags Any (Value = 'red') AND Scores Any (Value = 12)"));

        var collections = resolved.ListBindings().Where(entry => entry.IsCollection).Select(entry => entry.Key);
        Assert.Equal(["Dates", "Owners", "Readings", "Scores", "Shades", "Tags"], collections);
    }

    /// <summary>A byte array is a blob to a database, not a collection anything could quantify over</summary>
    [Fact]
    public void AByteArrayIsNotQuantified()
    {
        var resolved = Bins().WithWeequery().BindResolve();

        Assert.DoesNotContain(resolved.ListBindings(), entry => (entry.Key == "Photo") && entry.IsCollection);
        Assert.Contains(resolved.ListBindings(), entry => entry.Key == "Photo");
    }

    /// <summary>
    /// The key stays an ordinary property as well, so the null test and the index it answered before still work
    /// </summary>
    [Fact]
    public void TheCollectionIsStillAPropertyToo()
    {
        var resolved = Bins().WithWeequery().BindResolve();

        Assert.Equal([3], Ids(resolved, "Tags IsNull"));
        Assert.Equal([2], Ids(resolved, "Tags[0] = 'blue'"));
    }

    [Theory]
    [InlineData("Tags[]")]
    [InlineData("Tags[].")]
    [InlineData("Tags[].Value")]
    public void IgnorePathsCanTakeTheQuantifierAway(string ignore)
    {
        var settings = BindingResolutionSettings.Default with { IgnorePaths = [ignore] };
        var resolved = Bins().WithWeequery().BindResolve(settings: settings);

        Assert.DoesNotContain(resolved.ListBindings(), entry => (entry.Key == "Tags") && entry.IsCollection);
        Assert.Equal([3], Ids(resolved, "Tags IsNull"));
    }

    [Fact]
    public void RemovingValueRemovesTheCollection()
    {
        var resolved = Bins().WithWeequery().BindResolve().RemoveBinding("Tags[].Value");

        Assert.DoesNotContain(resolved.ListBindings(), entry => (entry.Key == "Tags") && entry.IsCollection);
    }

    // ---------- reading it back ----------

    /// <summary>
    /// A collection of objects cannot be projected, but a collection of values is one column to a provider that
    /// maps it, and it projected before it could be quantified over
    /// </summary>
    [Fact]
    public void ACollectionOfValuesStillProjects()
    {
        var row = Bins().WithWeequery().BindResolve().ApplyCondition("Id = 1").ApplyProjection("Id, Tags").BuildProjected().Single();

        Assert.Equal(new List<string?> { "red", "fragile" }, row["Tags"]);
    }

    [Fact]
    public void TheDefaultProjectionStillReadsIt()
    {
        var row = Bins().WithWeequery().BindResolve().ApplyCondition("Id = 1").BuildProjected().Single();

        Assert.Contains("Tags", row.Keys);
    }

    // ---------- the condition itself ----------

    [Fact]
    public void ItReadsBackAsTheTextItWasWrittenAs()
    {
        var written = ConditionFunctions.ParseQuery("Tags Any (Value = 'red')")!.ToString()!;

        Assert.Equal(written, ConditionFunctions.ParseQuery(written)!.ToString());
        Assert.Contains("Value", written, StringComparison.Ordinal);
    }

    [Fact]
    public void ItCrossesTheWireAsAnyOtherQuantifierDoes()
    {
        var condition = new QuantifiedCondition(Operator.Any, "Scores", new OneValueCondition<int>(Operator.Equals, QuantifiedCondition.ValueKey, 7));

        var json = JsonSerializer.Serialize(condition.Pack());
        var back = JsonSerializer.Deserialize<PackedCondition>(json)!.Unpack();

        Assert.Equal([2], Bound().ApplyCondition(back).Build().Select(bin => bin.Id));
    }
}
