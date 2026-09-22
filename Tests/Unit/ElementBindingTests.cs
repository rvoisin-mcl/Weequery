using Weequery;

namespace Tests.Unit;

public class Slot
{
    public int Weight { get; set; }
    public string Label { get; set; } = "";
}

public class Box
{
    public int Id { get; set; }
    public List<Slot>? Slots { get; set; }
    public List<string>? Labels { get; set; }
    public Dictionary<string, int>? Tallies { get; set; }
    public int[]? Scores { get; set; }
}

/// <summary>
/// Binding one element of a collection, where the application picks the element and the caller never learns
/// there was a collection at all.
/// <para>
/// The string path form always took an index. What these are mostly about is the selector, which did not: it
/// walks the member chain, and an index is not a member, so <c>x =&gt; x.Slots[0].Weight</c> was refused with a
/// message about selecting a property. The compiler writes an index three different ways depending on what is
/// being indexed, so all three are read.
/// </para>
/// <para>
/// This is the other half of <see cref="CollectionIndexTests"/>. There the caller names the index in the query
/// and the allow-list holds the whole collection; here the allow-list holds one element and nothing else.
/// </para>
/// </summary>
public class ElementBindingTests
{
    private static IQueryable<Box> Boxes()
    {
        return new List<Box>
        {
            new()
            {
                Id = 1,
                Slots = [new() { Weight = 50, Label = "heavy" }, new() { Weight = 5 }],
                Labels = ["first", "second"],
                Tallies = new() { ["apples"] = 9 },
                Scores = [10, 20],
            },
            new()
            {
                Id = 2,
                Slots = [new() { Weight = 1, Label = "light" }],
                Labels = ["only"],
                Tallies = new() { ["apples"] = 1 },
                Scores = [1],
            },
            new() { Id = 3 },   // every collection null
        }.AsQueryable();
    }

    private static int[] Ids(Func<Inquiry<Box>, Inquiry<Box>> bind, string query)
    {
        return [.. bind(Boxes().WithWeequery()).ApplyCondition(query).Build().ToList().Select(box => box.Id)];
    }

    // ---------- a selector reaching an element ----------

    /// <summary>
    /// The shape anyone would try first, and the one that used to be refused. A list indexes through a call to
    /// the indexer's getter, which is not a member, so the walk had to learn to step over it.
    /// </summary>
    [Fact]
    public void ASelectorCanIndexAListAndCarryOn()
    {
        Assert.Equal([1], Ids(box => box.BindProperty(b => b.Slots![0].Weight, "FirstWeight"), "FirstWeight > 10"));
        Assert.Equal([1], Ids(box => box.BindProperty(b => b.Slots![0].Label, "FirstLabel"), "FirstLabel = 'heavy'"));
    }

    [Fact]
    public void ASelectorCanEndAtTheElement()
    {
        Assert.Equal([1], Ids(box => box.BindProperty(b => b.Labels![1], "Second"), "Second = 'second'"));
    }

    /// <summary>An array indexes through its own node rather than through a call, so it is a separate shape</summary>
    [Fact]
    public void ASelectorCanIndexAnArray()
    {
        Assert.Equal([1], Ids(box => box.BindProperty(b => b.Scores![1], "Second"), "Second = 20"));
    }

    /// <summary>And a dictionary indexes by key, which is the same call shape a list uses</summary>
    [Fact]
    public void ASelectorCanIndexADictionary()
    {
        Assert.Equal([1], Ids(box => box.BindProperty(b => b.Tallies!["apples"], "Apples"), "Apples > 5"));
    }

    [Fact]
    public void ASelectorCanIndexMoreThanOnceAlongAPath()
    {
        // Slots[0] is an element, and its own properties are reachable from there
        Assert.Equal([1, 2], Ids(box => box.BindProperty(b => b.Slots![0].Weight, "W"), "W IsNotNull"));
    }

    // ---------- what the element behaves as ----------

    /// <summary>
    /// The same rule as everywhere else: an element that is not there is a null, whether the collection is short
    /// or missing entirely. Box 3 has no lists at all and box 2 has one slot.
    /// </summary>
    [Fact]
    public void AnElementThatIsNotThereIsANull()
    {
        // Box 2 holds one label and box 3 holds no list at all, and both are the same answer
        Assert.Equal([2, 3], Ids(box => box.BindProperty(b => b.Labels![1], "Second"), "Second IsNull"));
        Assert.Equal([1], Ids(box => box.BindProperty(b => b.Slots![1].Weight, "SecondWeight"), "SecondWeight IsNotNull"));
    }

    [Fact]
    public void ANegativeOperatorDoesNotCatchAMissingElement()
    {
        Assert.Equal([1], Ids(box => box.BindProperty(b => b.Labels![1], "Second"), "Second <> 'nothing'"));
    }

    // ---------- what the caller can see ----------

    /// <summary>
    /// The point of binding an element rather than the collection: the allow-list holds one thing, and the
    /// collection it came out of is not answerable at all
    /// </summary>
    [Fact]
    public void BindingAnElementDoesNotBindTheCollection()
    {
        var inquiry = Boxes().WithWeequery().BindProperty(box => box.Labels![0], "First");

        Assert.Throws<WeequeryException>(() => inquiry.ApplyCondition("Labels IsNull").Build().ToList());
        Assert.Throws<WeequeryException>(() => inquiry.ApplyCondition("Labels[0] IsNull").Build().ToList());
    }

    /// <summary>
    /// An element has to be given a name, because the one it would derive cannot be a key.
    /// <para>
    /// The derived key would be the path, "Labels[0]", and that is the same text a condition writes to ask for
    /// element zero of a binding called Labels. The parser cannot tell the two apart and would have to pick, so
    /// neither is allowed to be a key and the caller says what to call it. The message says as much, since the
    /// general rule about punctuation would not explain it.
    /// </para>
    /// </summary>
    [Fact]
    public void AnElementHasToBeGivenAKey()
    {
        var error = Assert.Throws<WeequeryException>(() => Boxes().WithWeequery().BindProperty(box => box.Labels![0]));

        Assert.Equal(WeequeryError.KeyInvalid, error.Error);
        Assert.Contains("brackets indicate a single element of a collection", error.Message);
    }

    /// <summary>
    /// And an explicit key holding brackets is refused for exactly the same reason, rather than because it is
    /// punctuation
    /// </summary>
    [Fact]
    public void AKeyCannotCarryAnIndexEither()
    {
        var error = Assert.Throws<WeequeryException>(
            () => Boxes().WithWeequery().BindProperty(box => box.Labels![0], "Labels[0]"));

        Assert.Equal(WeequeryError.KeyInvalid, error.Error);
    }

    // ---------- the segments form ----------

    /// <summary>
    /// Segments exist for a path the compiler cannot write. A bracketed one is an index and joins onto the
    /// segment in front of it rather than after a period, which would be a step with no property name.
    /// </summary>
    [Fact]
    public void ABracketedSegmentIsAnIndex()
    {
        Assert.Equal([1], Ids(box => box.BindProperty(b => b.Slots, ["[0]", "Weight"], "W"), "W > 10"));
    }

    /// <summary>
    /// And a bare one is not. A segment is a property name, so "0" asks for a property called 0 and is refused
    /// as one, rather than quietly being read as a position.
    /// </summary>
    [Fact]
    public void ABareSegmentIsStillAPropertyName()
    {
        Assert.Throws<WeequeryException>(() => Boxes().WithWeequery().BindProperty(b => b.Slots, ["0", "Weight"], "W"));
    }

    // ---------- what is refused ----------

    /// <summary>
    /// A binding is made once, not per row, so an index read from a variable would freeze whatever it happened to
    /// be and read as though it followed the variable. Refused with the two things to do instead.
    /// </summary>
    [Fact]
    public void AnIndexFromAVariableIsRefused()
    {
        var position = 1;

        var error = Assert.Throws<WeequeryException>(
            () => Boxes().WithWeequery().BindProperty(box => box.Labels![position], "Chosen"));

        Assert.Equal(WeequeryError.PathInvalid, error.Error);
    }

    [Fact]
    public void IndexingSomethingThatIsNotACollectionIsRefused()
    {
        var error = Assert.Throws<WeequeryException>(() => Boxes().WithWeequery().BindProperty("Id[0]", "Key"));

        Assert.Equal(WeequeryError.PathInvalid, error.Error);
    }

    // ---------- the two routes agree ----------

    /// <summary>
    /// The selector is a way of writing the path with the compiler checking it, so it has to arrive at the same
    /// binding the path does
    /// </summary>
    [Fact]
    public void ASelectorAndAStringPathBindTheSameThing()
    {
        var viaSelector = Ids(box => box.BindProperty(b => b.Slots![0].Weight, "W"), "W > 10");
        var viaPath = Ids(box => box.BindProperty("Slots[0].Weight", "W"), "W > 10");

        Assert.Equal([1], viaSelector);
        Assert.Equal(viaSelector, viaPath);
    }
}
