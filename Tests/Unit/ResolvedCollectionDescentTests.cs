using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>A collection one level below the entity, so the two depths can be told apart</summary>
public class Warehouse
{
    public int Id { get; set; }
    public Rack? Rack { get; set; }
}

public class Rack
{
    public string Note { get; set; } = "";
    public List<Slot>? Slots { get; set; }
}

/// <summary>
/// A collection one level down whose element has somewhere further to go, which is what makes the promotion
/// visible: Yard -> Bay -> Cartons -> Carton -> Stamp -> Mark.
/// </summary>
public class Yard
{
    public Bay? Bay { get; set; }
}

public class Bay
{
    public List<Carton>? Cartons { get; set; }
}

public class Carton
{
    public string Code { get; set; } = "";
    public Stamp? Stamp { get; set; }
}

public class Stamp
{
    public string Mark { get; set; } = "";
}

/// <summary>
/// Two collections of the same element type hanging off one entity, which is where a subtraction aimed at one
/// of them has to be told from a subtraction aimed at both.
/// </summary>
public class Hub
{
    public List<Haul>? Inbound { get; set; }
    public List<Haul>? Outbound { get; set; }
}

public class Haul
{
    public string Code { get; set; } = "";
    public Halt? Halt { get; set; }
}

public class Halt
{
    public string Town { get; set; } = "";
}

/// <summary>
/// Resolution entering a collection, and one key answering everything that can be asked about one.
/// <para>
/// A collection is three questions, not one: whether it is there, what one element of it holds, and whether any
/// element satisfies something. They used to need two keys and a rule that a key could only be one of them.
/// These are mostly that the one key answers all three, and that the two depths are counted apart.
/// </para>
/// </summary>
public class ResolvedCollectionDescentTests
{
    private static IQueryable<Box> Boxes()
    {
        return new List<Box>
        {
            new() { Id = 1, Slots = [new() { Weight = 50, Label = "heavy" }, new() { Weight = 5, Label = "light" }], Tallies = new() { ["apples"] = 9 } },
            new() { Id = 2, Slots = [new() { Weight = 5, Label = "light" }], Tallies = new() { ["apples"] = 1 } },
            new() { Id = 3, Slots = [] },
            new() { Id = 4 },
        }.AsQueryable();
    }

    private static Inquiry<Box> Resolved(int maxDepth = 0)
    {
        return Boxes().WithWeequery().BindResolve(maxDepth);
    }

    // ---------- one key, three questions ----------

    /// <summary>
    /// The point of the whole thing: one key is both bindings at once, so it answers a question about the
    /// collection and a question about one element of it, and neither call had to know about the other.
    /// </summary>
    /// <remarks>
    /// "Is it empty" is <c>None</c> rather than a null test. A quantifier is total, so it answers for an absent
    /// collection as readily as an empty one, where a null test on the collection itself distinguishes two
    /// things a database will not.
    /// </remarks>
    [Fact]
    public void OneKeyIsBothBindings()
    {
        Assert.Equal(1, Resolved().ApplyCondition("Slots Any (Weight > 10)").Build().Count());
        Assert.Equal(2, Resolved().ApplyCondition("Slots[0] IsNotNull").Build().Count());
        Assert.Equal(1, Resolved().ApplyCondition("Tallies[apples] > 5").Build().Count());
    }

    /// <summary>
    /// An element of a collection of objects that is not there reads as a null, the same as an element of any
    /// other collection.
    /// </summary>
    /// <remarks>
    /// This one used to throw. A collection whose element is neither a scalar nor a string is built by
    /// ObjectExpressionBuilder, and that was the one builder testing the accessor directly rather than going
    /// through the binding's NotNullCheck, so nothing short circuited in front of the index and reading past the
    /// end of a list raised ArgumentOutOfRangeException instead of answering. Every other element type went
    /// through the shared path and was always correct, which is why it survived: nothing in the model had a
    /// collection of objects to index.
    /// </remarks>
    [Fact]
    public void AMissingObjectElementIsANullRatherThanAThrow()
    {
        // box 3 holds an empty list and box 4 holds none, and both are the same answer
        Assert.Equal([3, 4], Resolved().ApplyCondition("Slots[0] IsNull").Build().Select(box => box.Id).ToList());
        Assert.Equal([1, 2], Resolved().ApplyCondition("Slots[0] IsNotNull").Build().Select(box => box.Id).ToList());

        // and past the end of every one of them is a null everywhere
        Assert.Equal([1, 2, 3, 4], Resolved().ApplyCondition("Slots[9] IsNull").Build().Select(box => box.Id).ToList());
        Assert.Empty(Resolved().ApplyCondition("Slots[9] IsNotNull").Build().ToList());
    }

    /// <summary>
    /// And it is not the index that makes it so: a path through a link that can be missing is guarded the same
    /// way, which is what NotNullCheck is for
    /// </summary>
    [Fact]
    public void TheGuardHoldsWithoutAnIndexToo()
    {
        var pallets = new List<Warehouse>
        {
            new() { Id = 1, Rack = new() { Note = "a", Slots = [new() { Weight = 1 }] } },
            new() { Id = 2 },
        }.AsQueryable();

        var inquiry = pallets.WithWeequery().BindResolve();

        Assert.Equal([1], inquiry.ApplyCondition("Rack.Slots[0] IsNotNull").Build().Select(p => p.Id).ToList());
        Assert.Equal([2], inquiry.ApplyCondition("Rack.Slots[0] IsNull").Build().Select(p => p.Id).ToList());
    }

    [Fact]
    public void TheQuantifiersAllAnswer()
    {
        static List<int> Ask(string query)
        {
            return [.. Resolved().ApplyCondition(query).Build().Select(box => box.Id)];
        }

        Assert.Equal([1], Ask("Slots Any (Weight > 10)"));
        Assert.Equal([1, 2], Ask("Slots Any (Label = 'light')"));
        Assert.Equal([2, 3, 4], Ask("Slots None (Weight > 10)"));
        Assert.Equal([2, 3, 4], Ask("Slots All (Weight < 10)"));   // true of the empty one and of the missing one
    }

    /// <summary>
    /// A dictionary is not something a quantifier can reach into, so entering collections leaves it exactly as
    /// it was: an ordinary binding that an index reads one value out of
    /// </summary>
    [Fact]
    public void ANonQuantifiableCollectionIsUntouched()
    {
        Assert.Equal(1, Resolved().ApplyCondition("Tallies[apples] > 5").Build().Count());

        Assert.Throws<WeequeryException>(() => Resolved().ApplyCondition("Tallies Any (Weight > 1)").Build().ToList());
    }

    // ---------- what it decides is quantifiable ----------

    [Fact]
    public void OnlyACollectionOfObjectsIsEntered()
    {
        var found = Inquiry<Box>.ResolveBindableCollections();

        Assert.Equal(["Slots"], found.Select(collection => collection.Key));
        Assert.Equal(typeof(Slot), Assert.Single(found).ElementType);
    }

    /// <summary>
    /// A list of strings, a dictionary and an array of numbers have no property of an element to name
    /// </summary>
    [Theory]
    [InlineData("Labels")]
    [InlineData("Tallies")]
    [InlineData("Scores")]
    public void ASequenceWithNothingToNameInsideIsNotEntered(string key)
    {
        Assert.DoesNotContain(key, Inquiry<Box>.ResolveBindableCollections().Select(collection => collection.Key));
    }

    /// <summary>
    /// The property list is what it always was, collections included: entering one adds a second binding rather
    /// than replacing the first
    /// </summary>
    [Fact]
    public void ResolveBindablesIsUnchanged()
    {
        var keys = Inquiry<Box>.ResolveBindables().Select(request => request.Key).ToList();

        Assert.Contains("Slots", keys);
        Assert.Contains("Labels", keys);
        Assert.Contains("Tallies", keys);
    }

    // ---------- one depth, spent on both ----------

    /// <summary>
    /// The entity's own collections are free: reaching one costs nothing whatever the budget, and the whole of
    /// the budget is then what its element gets
    /// </summary>
    [Fact]
    public void TheRootsOwnCollectionsAreFree()
    {
        var own = Assert.Single(Inquiry<Rack>.ResolveBindableCollections(maxDepth: 0));

        Assert.Equal("Slots", own.Key);
        Assert.Equal(["Label", "Weight"], own.Elements.Select(request => request.Key));
    }

    /// <summary>
    /// A collection below the root is not, and is an ordinary descent paid for out of what is left.
    /// <para>
    /// Rack.Slots sits one level down. At a budget of 1 that level is what got the walk to Rack at all, so
    /// there is nothing left to enter the collection with and it is not gathered; 2 reaches it and leaves 0,
    /// which is the element's own properties.
    /// </para>
    /// </summary>
    [Fact]
    public void ACollectionBelowTheRootIsAnOrdinaryDescent()
    {
        Assert.Empty(Inquiry<Warehouse>.ResolveBindableCollections(maxDepth: 0));
        Assert.Empty(Inquiry<Warehouse>.ResolveBindableCollections(maxDepth: 1));

        var nested = Assert.Single(Inquiry<Warehouse>.ResolveBindableCollections(maxDepth: 2));

        Assert.Equal("Rack.Slots", nested.Key);
        Assert.Equal(["Label", "Weight"], nested.Elements.Select(request => request.Key));
    }

    /// <summary>
    /// Which is to say the promotion does not compound.
    /// <para>
    /// Cartons off its own root at a budget of 1 is free to enter and spends the 1 on the element, so Stamp.Mark
    /// is reached. The same collection one level down at a budget of 2 spends one getting to Bay and one entering
    /// the collection, leaving nothing, so it stops at the element's own properties. Granted at every level the
    /// promotion would pay for itself again on each one and the second hop would come back.
    /// </para>
    /// </summary>
    [Fact]
    public void ThePromotionIsTheRootsAloneAndDoesNotCompound()
    {
        var free = Assert.Single(Inquiry<Bay>.ResolveBindableCollections(maxDepth: 1));

        Assert.Equal(["Code", "Stamp", "Stamp.Mark"], free.Elements.Select(request => request.Key));

        // One level down, and one more of budget to pay for it: the same place reached, and nothing left over
        var paidFor = Assert.Single(Inquiry<Yard>.ResolveBindableCollections(maxDepth: 2));

        Assert.Equal("Bay.Cartons", paidFor.Key);
        Assert.Equal(["Code", "Stamp"], paidFor.Elements.Select(request => request.Key));

        // And it takes a third to buy the hop the root got for nothing
        var deeper = Assert.Single(Inquiry<Yard>.ResolveBindableCollections(maxDepth: 3));

        Assert.Equal(["Code", "Stamp", "Stamp.Mark"], deeper.Elements.Select(request => request.Key));
    }

    /// <summary>
    /// Zero reaches an element's own properties, and the second hop of a link table needs one more
    /// </summary>
    [Fact]
    public void TheDepthDecidesTheSecondHop()
    {
        var shallow = MinionTestData.Minions().WithWeequery().BindResolve(0);
        var deeper = MinionTestData.Minions().WithWeequery().BindResolve(1);

        Assert.True(shallow.ApplyCondition("LairAssignments Any (Lair IsNull)").Validate().IsValid);
        Assert.False(shallow.ApplyCondition("LairAssignments Any (Lair.Name StartsWith 'V')").Validate().IsValid);

        Assert.True(deeper.ApplyCondition("LairAssignments Any (Lair.Name StartsWith 'V')").Validate().IsValid);
    }

    [Theory]
    [InlineData(-4)]
    [InlineData(400)]
    public void TheDepthIsBounded(int depth)
    {
        Assert.NotEmpty(Inquiry<Box>.ResolveBindableCollections(depth));
    }

    // ---------- projection ----------

    /// <summary>
    /// A collection still cannot be read back, holding many values where a column holds one. One element of it
    /// can, and that is the same key, so only the bare one is refused.
    /// </summary>
    [Fact]
    public void TheBareKeyDoesNotProjectAndAnIndexedOneDoes()
    {
        var rows = Resolved().ApplyCondition("Tallies IsNotNull").ApplyProjection("Id, Tallies[apples]").BuildProjected().ToList();

        Assert.Equal(2, rows.Count);
        Assert.Equal(["Id", "Tallies[apples]"], rows[0].Keys);

        var error = Assert.Throws<WeequeryException>(() => Resolved().ApplyProjection("Slots").BuildProjected().ToList());

        Assert.Equal(WeequeryError.OperatorUnsupported, error.Error);
    }

    // ---------- one key, one property ----------

    /// <summary>
    /// Both bindings under one key are allowed because they are the same property. A different property wanting
    /// the name is refused exactly as two property bindings would be.
    /// </summary>
    [Fact]
    public void ADifferentPropertyUnderTheSameKeyIsStillRefused()
    {
        var error = Assert.Throws<WeequeryException>(() =>
            Boxes().WithWeequery()
                .BindProperty(box => box.Id, "Slots")
                .BindCollection(box => box.Slots!, "Slots"));

        Assert.Equal(WeequeryError.KeyTaken, error.Error);
    }

    /// <summary>
    /// One property spelled two ways is still one property, so both bindings are allowed under the one key.
    /// </summary>
    /// <remarks>
    /// A path is matched without regard to case, but the string form used to keep whatever the caller typed
    /// while a selector produced the member's own spelling, so the two arrived as different paths and the
    /// duplicate check read them as two different properties. Paths are canonicalised where the binding is
    /// built now, so there is one spelling by the time anything compares them.
    /// </remarks>
    [Fact]
    public void OnePropertySpelledTwoWaysIsStillOneProperty()
    {
        var inquiry = Boxes().WithWeequery()
            .BindProperty("slots", "Slots")
            .BindCollection(box => box.Slots!, "Slots");

        Assert.Equal(1, inquiry.ApplyCondition("Slots Any (Weight > 10)").Build().Count());
    }

    /// <summary>
    /// Every segment of a nested path, not only the first
    /// </summary>
    [Fact]
    public void EverySegmentIsCanonicalised()
    {
        var pallets = new List<Warehouse>
        {
            new() { Id = 1, Rack = new() { Note = "here", Slots = [] } },
        }.AsQueryable();

        Assert.Equal(1, pallets.WithWeequery().BindProperty("rack.note", "Note").ApplyCondition("Note = 'here'").Build().Count());
    }

    /// <summary>
    /// An index is data rather than a member name, so its own casing survives untouched. A dictionary keyed
    /// "Apples" is not the same dictionary keyed "apples", and canonicalising the path must not pretend it is.
    /// </summary>
    [Fact]
    public void AnIndexKeepsItsOwnCasing()
    {
        var boxes = new List<Box> { new() { Id = 1, Tallies = new() { ["Apples"] = 9 } } }.AsQueryable();

        Assert.Equal(1, boxes.WithWeequery().BindProperty("tallies[Apples]", "T").ApplyCondition("T > 1").Build().Count());
        Assert.Equal(0, boxes.WithWeequery().BindProperty("tallies[apples]", "T").ApplyCondition("T > 1").Build().Count());
    }

    /// <summary>
    /// And binding the same collection twice is still one binding too many
    /// </summary>
    [Fact]
    public void TheSameCollectionTwiceIsRefused()
    {
        var error = Assert.Throws<WeequeryException>(() =>
            Boxes().WithWeequery()
                .BindCollection(box => box.Slots!, "Slots")
                .BindCollection(box => box.Slots!, "Slots"));

        Assert.Equal(WeequeryError.KeyTaken, error.Error);
    }

    /// <summary>
    /// Declaring a collection over a resolve that already entered it is the same property under the same key, so
    /// it is the ordinary refusal rather than a collision
    /// </summary>
    [Fact]
    public void ResolvingThenDeclaringTheSameCollectionIsRefusedAsADuplicate()
    {
        var error = Assert.Throws<WeequeryException>(() =>
            Boxes().WithWeequery().BindResolve().BindCollection(box => box.Slots!, "Slots"));

        Assert.Equal(WeequeryError.KeyTaken, error.Error);
    }

    // ---------- subtracting inside a collection ----------

    private static IEnumerable<string> Inside(string collection, params string[] ignore)
    {
        var settings = BindingResolutionSettings.Default with { IgnorePaths = [.. ignore] };

        return Inquiry<Hub>.ResolveBindableCollections(1, settings)
            .Where(found => found.Key == collection)
            .SelectMany(found => found.Elements.Select(request => request.Key));
    }

    /// <summary>
    /// The collection's own stop: bind it, and resolve nothing inside it.
    /// <para>
    /// The property binding is untouched, so the key stays there to be null tested and indexed, and only the
    /// quantifier goes. That is what separates it from naming the collection with no marker, which takes the
    /// name away altogether.
    /// </para>
    /// </summary>
    [Fact]
    public void TheCollectionStopLeavesItBoundAndUnopened()
    {
        var settings = BindingResolutionSettings.Default with { IgnorePaths = ["Inbound[]."] };

        var bound = new List<Hub>().AsQueryable().WithWeequery().BindResolve(1, settings).ListBindings();

        // Still a key, and no longer a collection
        var inbound = bound.Single(entry => entry.Key == "Inbound");

        Assert.False(inbound.IsCollection);
        Assert.DoesNotContain(bound, entry => entry.ElementOf == "Inbound");

        // And the other one is untouched, which is the whole point of naming a path rather than a type
        Assert.True(bound.Single(entry => entry.Key == "Outbound").IsCollection);
        Assert.Contains(bound, entry => entry.ElementOf == "Outbound");
    }

    /// <summary>
    /// With or without the trailing period, there being nothing between the two: an element has no half to keep
    /// </summary>
    [Fact]
    public void TheStopReadsTheSameWithOrWithoutThePeriod()
    {
        Assert.Empty(Inside("Inbound", "Inbound[]."));
        Assert.Empty(Inside("Inbound", "Inbound[]"));
    }

    /// <summary>
    /// A path inside one collection is subtracted from that collection and no other, which is what the older
    /// element-relative spelling could not say
    /// </summary>
    [Fact]
    public void APathInsideOneCollectionIsSubtractedFromThatOneAlone()
    {
        Assert.Equal(["Code", "Halt", "Halt.Town"], Inside("Outbound", "Inbound[].Halt"));
        Assert.Equal(["Code"], Inside("Inbound", "Inbound[].Halt"));

        // And the trailing period stops at it rather than removing it, exactly as it does on the outside
        Assert.Equal(["Code", "Halt"], Inside("Inbound", "Inbound[].Halt."));
        Assert.Equal(["Code", "Halt", "Halt.Town"], Inside("Outbound", "Inbound[].Halt."));
    }

    /// <summary>
    /// An entry aimed at a collection that is not this one does not leak into it
    /// </summary>
    [Fact]
    public void AnEntryAimedAtAnotherCollectionDoesNotLeak()
    {
        Assert.Equal(["Code", "Halt", "Halt.Town"], Inside("Inbound", "Nowhere[].Halt"));
    }

    /// <summary>
    /// And the element-relative spelling still means what it always did, which is every collection at once
    /// </summary>
    [Fact]
    public void AnElementRelativeEntryStillAppliesToEveryCollection()
    {
        Assert.Equal(["Code"], Inside("Inbound", "Halt"));
        Assert.Equal(["Code"], Inside("Outbound", "Halt"));
    }

    /// <summary>
    /// The two spellings are subtractions rather than alternatives, so naming both takes both away
    /// </summary>
    [Fact]
    public void TheTwoSpellingsAddUp()
    {
        Assert.Equal(["Code"], Inside("Inbound", "Inbound[].Halt", "Halt"));
        Assert.Equal(["Code"], Inside("Outbound", "Inbound[].Halt", "Halt"));
    }
}
