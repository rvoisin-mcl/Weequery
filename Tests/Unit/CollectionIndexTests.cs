using System.Text.Json;
using Weequery;
using Weequery.Interfaces;

namespace Tests.Unit;

public class Basket
{
    public int Id { get; set; }
    public List<string>? Items { get; set; }
    public Dictionary<string, int>? Tallies { get; set; }
    public int[]? Scores { get; set; }
    public IReadOnlyList<int>? Readings { get; set; }
    public string Label { get; set; } = "";
}

/// <summary>
/// Testing one element of a bound collection, which a condition names with brackets after the field:
/// <c>Tallies[apples] &gt; 5</c>.
/// <para>
/// The whole of the semantics is that an element which is not there behaves as a null, and nulls in this library
/// are not a detail: a missing element satisfies nothing except IsNull, the negative operators do not catch it,
/// and Not brings it back because Not negates the guard along with the test. That is the same partition an
/// ordinary nullable property gives, and these check it holds here too.
/// </para>
/// </summary>
public class CollectionIndexTests
{
    private static IQueryable<Basket> Baskets()
    {
        return new List<Basket>
        {
            new() { Id = 1, Items = ["apple", "pear"], Tallies = new() { ["apples"] = 7 }, Scores = [10, 20], Readings = [3], Label = "first" },
            new() { Id = 2, Items = ["fig"], Tallies = new() { ["apples"] = 2 }, Scores = [1], Readings = [], Label = "second" },
            new() { Id = 3, Items = null, Tallies = null, Scores = null, Readings = null, Label = "third" },
        }.AsQueryable();
    }

    private static Inquiry<Basket> Bound()
    {
        return Baskets()
            .WithWeequery()
            .BindProperty(basket => basket.Id)
            .BindProperty(basket => basket.Items)
            .BindProperty(basket => basket.Tallies)
            .BindProperty(basket => basket.Scores)
            .BindProperty(basket => basket.Readings)
            .BindProperty(basket => basket.Label);
    }

    private static int[] Ids(string query)
    {
        return [.. Bound().ApplyCondition(query).Build().ToList().Select(basket => basket.Id)];
    }

    // ---------- reaching an element ----------

    [Fact]
    public void ADictionaryIsIndexedByKey()
    {
        Assert.Equal([1], Ids("Tallies[apples] > 5"));
        Assert.Equal([1, 2], Ids("Tallies[apples] > 0"));
    }

    [Fact]
    public void AListIsIndexedByPosition()
    {
        Assert.Equal([1], Ids("Items[1] = 'pear'"));
        Assert.Equal([1, 2], Ids("Items[0] IsNotNull"));
    }

    [Fact]
    public void AnArrayIsIndexedByPosition()
    {
        Assert.Equal([1], Ids("Scores[0] > 5"));
    }

    /// <summary>
    /// Read off the interfaces rather than the concrete type, so a property typed as one indexes the same way
    /// </summary>
    [Fact]
    public void AReadOnlyListIsIndexedToo()
    {
        Assert.Equal([1], Ids("Readings[0] = 3"));
    }

    // ---------- the whole point: a missing element is a null ----------

    /// <summary>
    /// Three ways to be missing, and all three are the same answer: no key, past the end, and no collection at
    /// all. None of them is an error and none of them is a default value.
    /// </summary>
    [Theory]
    [InlineData("Tallies[pears] > 0")]   // the dictionary is there and has no such key
    [InlineData("Items[5] IsNotNull")]   // the list is there and is shorter than that
    [InlineData("Scores[0] > 0")]        // basket 3 has no array at all
    public void AMissingElementSatisfiesNothing(string query)
    {
        Assert.DoesNotContain(3, Ids(query));
    }

    [Fact]
    public void AMissingElementIsCaughtByIsNull()
    {
        // every basket: one has no such key, one has no such key, one has no dictionary
        Assert.Equal([1, 2, 3], Ids("Tallies[pears] IsNull"));

        // and past the end of a list is the same answer as having no list
        Assert.Equal([1, 2, 3], Ids("Items[9] IsNull"));
    }

    /// <summary>
    /// The negative operators do not catch it, exactly as they do not catch a null property. It is not "not 99",
    /// it is unknown.
    /// </summary>
    [Fact]
    public void ANegativeOperatorDoesNotCatchAMissingElement()
    {
        Assert.Empty(Ids("Tallies[pears] <> 99"));
        Assert.Empty(Ids("Items[9] DoesNotContain 'x'"));
    }

    /// <summary>
    /// And Not does, because it negates the guard along with the test. The two are different questions, here for
    /// the same reason they are different questions about a nullable property.
    /// </summary>
    [Fact]
    public void NotBringsAMissingElementBack()
    {
        Assert.Empty(Ids("Tallies[pears] <> 99"));
        Assert.Equal([1, 2, 3], Ids("NOT (Tallies[pears] = 99)"));
    }

    /// <summary>
    /// The collection being null is checked before it is asked anything, so reading an element of nothing never
    /// happens rather than happening and throwing
    /// </summary>
    [Fact]
    public void ANullCollectionIsGuardedRatherThanThrown()
    {
        Assert.Equal([1], Ids("Tallies[apples] > 5"));
        Assert.Equal([1], Ids("Scores[1] = 20"));
    }

    [Fact]
    public void AnEmptyCollectionIsNotAnError()
    {
        // basket 2 has an empty Readings, which is present and holds nothing at 0
        Assert.Equal([1], Ids("Readings[0] IsNotNull"));
    }

    // ---------- the query language ----------

    [Fact]
    public void AnIndexIsWrittenAsBracketsAfterTheField()
    {
        Assert.Equal("([Tallies][apples] > '5')", ConditionFunctions.ParseQuery("Tallies[apples] > 5")!.ToQuery());
        Assert.Equal("([Items][0] IsNull)", ConditionFunctions.ParseQuery("Items[0] IsNull")!.ToQuery());
    }

    [Theory]
    [InlineData("Tallies[apples] > 5")]
    [InlineData("Items[0] = 'apple'")]
    [InlineData("Scores[2] IsNull")]
    [InlineData("Tallies[apples] IsBetween (1, 9)")]
    [InlineData("Items[0] IsIn ('apple', 'fig')")]
    [InlineData("NOT (Tallies[apples] > 5)")]
    public void AnIndexedConditionRoundTrips(string query)
    {
        var once = ConditionFunctions.ParseQuery(query)!.ToQuery();

        Assert.Equal(once, ConditionFunctions.ParseQuery(once)!.ToQuery());
        Assert.Contains("[", once);
    }

    /// <summary>
    /// The bracketed field form and the index are two different brackets, and both read
    /// </summary>
    [Fact]
    public void ABracketedFieldTakesAnIndexToo()
    {
        Assert.Equal([1], Ids("[Tallies][apples] > 5"));
    }

    /// <summary>
    /// A key that would not survive as a bare word is quoted, on the same rule a value is
    /// </summary>
    [Fact]
    public void AnIndexIsQuotedWhenItNeedsToBe()
    {
        var condition = new OneValueCondition<int>(Operator.Equals, "Tallies", 1, "two words");

        var written = condition.ToQuery();

        Assert.Equal("([Tallies]['two words'] = 1)", written);
        Assert.Equal("two words", ((IBound)ConditionFunctions.ParseQuery(written)!).Index);
    }

    [Fact]
    public void AnIndexIsStillReadUnderTheStrictGrammar()
    {
        Assert.NotNull(ConditionFunctions.ParseQuery("Tallies[apples] > 5", QueryStyle.Native));
    }

    // ---------- across the wire ----------

    [Fact]
    public void TheIndexTravelsInThePackedForm()
    {
        var packed = JsonSerializer.Serialize(ConditionFunctions.ParseQuery("Tallies[apples] > 5")!.Pack());

        Assert.Equal("""{"Operator":6,"Field":"Tallies","Index":"apples","Values":["5"],"Conditions":[]}""", packed);
    }

    /// <summary>
    /// Nearly every condition ever sent names no index, so one that does not says nothing about it and a payload
    /// written before indexing existed is the payload it always was
    /// </summary>
    [Fact]
    public void AConditionWithNoIndexSaysNothingAboutOne()
    {
        var packed = JsonSerializer.Serialize(ConditionFunctions.ParseQuery("Tallies IsNull")!.Pack());

        Assert.DoesNotContain("Index", packed);
    }

    [Fact]
    public void AnIndexedConditionSurvivesTheWire()
    {
        var json = JsonSerializer.Serialize(ConditionFunctions.ParseQuery("Tallies[apples] > 5")!.Pack());

        var back = JsonSerializer.Deserialize<PackedCondition>(json)!.Unpack();

        Assert.Equal("apples", ((IBound)back).Index);
        Assert.Equal("([Tallies][apples] > '5')", back.ToQuery());
    }

    // ---------- what is refused ----------

    [Fact]
    public void IndexingSomethingThatIsNotACollectionIsRefused()
    {
        var error = Assert.Throws<WeequeryException>(() => Bound().ApplyCondition("Label[0] = 'x'").Build().ToList());

        Assert.Contains("cannot be indexed", error.Message);
    }

    [Fact]
    public void AnIndexThatIsNotOfTheKeyTypeIsRefused()
    {
        // Scores is an int[], so its index has to read as an int
        Assert.Throws<WeequeryException>(() => Bound().ApplyCondition("Scores[apples] > 1").Build().ToList());
    }

    [Fact]
    public void AnUnboundFieldIsStillUnboundWithAnIndexOnIt()
    {
        Assert.Throws<WeequeryException>(() => Bound().ApplyCondition("Nothing[0] = 1").Build().ToList());
    }

    [Fact]
    public void AnEmptyIndexIsRefused()
    {
        Assert.Throws<WeequeryException>(() => new OneValueCondition<int>(Operator.Equals, "Tallies", 1, string.Empty));
    }

    // ---------- the collection itself is still a binding ----------

    /// <summary>
    /// Indexing is something a condition asks for, not something the binding is. The same binding still answers
    /// the null tests about the collection as a whole.
    /// </summary>
    [Fact]
    public void TheCollectionItselfIsStillTestable()
    {
        Assert.Equal([3], Ids("Tallies IsNull"));
        Assert.Equal([1, 2], Ids("Tallies IsNotNull"));
    }
}
