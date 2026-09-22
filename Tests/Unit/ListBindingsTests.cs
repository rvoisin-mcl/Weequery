using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// Reading the allow-list back, which is the answer to "what may I ask about" without having to be refused
/// first.
/// <para>
/// Most of this is that the three things it reports are the three things that were bound. The rest is the
/// collections, which are the only place the report is not a straight copy of what is stored: one is held twice
/// over under the one key, and a caller counting names wants to hear the name once.
/// </para>
/// </summary>
public class ListBindingsTests
{
    private static IQueryable<Box> Boxes()
    {
        return new List<Box>().AsQueryable();
    }

    // ---------- the three things ----------

    [Fact]
    public void ItReportsTheKeyThePathAndTheUse()
    {
        var entry = Assert.Single(Boxes().WithWeequery().BindProperty(box => box.Id).ListBindings());

        Assert.Equal("Id", entry.Key);
        Assert.Equal("Id", entry.Path);
        Assert.Equal(BindingUse.All, entry.Use);
        Assert.False(entry.IsCollection);
        Assert.Null(entry.ElementOf);
        Assert.Null(entry.ElementKey);
    }

    /// <summary>
    /// The key is what a caller writes and the path is what it reaches, and a binding is free to keep them apart
    /// </summary>
    [Fact]
    public void AKeyAndAPathAreReportedApart()
    {
        var entry = Assert.Single(Boxes().WithWeequery().BindProperty(box => box.Id, "Identifier").ListBindings());

        Assert.Equal("Identifier", entry.Key);
        Assert.Equal("Id", entry.Path);
    }

    /// <summary>
    /// And the path comes back as the binding holds it, which is canonical rather than however it was spelled
    /// </summary>
    [Fact]
    public void ThePathComesBackCanonical()
    {
        var entry = Assert.Single(MinionTestData.Minions().WithWeequery()
            .BindProperty("lairassignments[0].lair.name", "Lair")
            .ListBindings());

        Assert.Equal("Lair", entry.Key);
        Assert.Equal("LairAssignments[0].Lair.Name", entry.Path);
    }

    /// <summary>
    /// A narrowed binding reports what is left of it, rather than what it was bound as
    /// </summary>
    [Fact]
    public void ItReportsTheUseAsItStands()
    {
        var bound = Boxes().WithWeequery()
            .BindProperty(box => box.Id, "Id", BindingUse.Projection)
            .BindProperty(box => box.Labels, "Labels")
            .RemoveBinding("Labels", BindingUse.Sort)
            .ListBindings();

        Assert.Equal(BindingUse.Projection, bound.Single(entry => entry.Key == "Id").Use);
        Assert.Equal(BindingUse.Test | BindingUse.Projection, bound.Single(entry => entry.Key == "Labels").Use);
    }

    /// <summary>
    /// A constant has no property to point at, so it reports the name it was bound under
    /// </summary>
    [Fact]
    public void AConstantReportsItsKeyAsItsPath()
    {
        var entry = Assert.Single(Boxes().WithWeequery().BindConstant("Tenant", 7).ListBindings());

        Assert.Equal("Tenant", entry.Key);
        Assert.Equal("Tenant", entry.Path);
        Assert.Equal(BindingUse.Test | BindingUse.Projection, entry.Use);
    }

    [Fact]
    public void WhatIsRemovedIsNoLongerListed()
    {
        var bound = Boxes().WithWeequery()
            .BindProperty(box => box.Id)
            .BindProperty(box => box.Labels)
            .RemoveBinding("Labels")
            .ListBindings();

        Assert.Equal(["Id"], bound.Select(entry => entry.Key));
    }

    // ---------- the collections ----------

    /// <summary>
    /// A declared collection is testable and nothing else, a quantifier being all there is to do with one
    /// </summary>
    [Fact]
    public void ADeclaredCollectionIsReportedAsTestable()
    {
        var bound = Boxes().WithWeequery()
            .BindCollection(box => box.Slots!, "Slots", inner => inner.BindProperty(slot => slot.Weight))
            .ListBindings();

        Assert.Equal(["Slots", "Slots[].Weight"], bound.Select(listed => listed.Key));

        var entry = bound[0];

        Assert.Equal("Slots", entry.Key);
        Assert.Equal("Slots", entry.Path);
        Assert.Equal(BindingUse.Test, entry.Use);
        Assert.True(entry.IsCollection);
    }

    /// <summary>
    /// One key held as both is still one key, and it reports what the two of them grant between them
    /// </summary>
    [Fact]
    public void AKeyHeldAsBothIsReportedOnceWithBothGrants()
    {
        var bound = Boxes().WithWeequery()
            .BindProperty(box => box.Slots!, "Slots", BindingUse.Projection)
            .BindCollection(box => box.Slots!, "Slots", inner => inner.BindProperty(slot => slot.Weight))
            .ListBindings();

        var entry = bound.Single(listed => listed.ElementOf is null);

        Assert.Equal("Slots", entry.Key);
        Assert.Equal(BindingUse.Projection | BindingUse.Test, entry.Use);
        Assert.True(entry.IsCollection);
    }

    /// <summary>
    /// Which is what resolution produces, so a resolved listing names each key once
    /// </summary>
    [Fact]
    public void AResolvedListingHasNoDuplicateKeys()
    {
        var bound = Boxes().WithWeequery().BindResolve().ListBindings();

        Assert.Equal(bound.Count, bound.Select(entry => entry.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(bound, entry => (entry.Key == "Slots") && entry.Use.HasFlag(BindingUse.Test) && entry.IsCollection);
    }

    /// <summary>
    /// And the flag is the only thing that tells one apart. A quantifier is a condition, so a collection grants
    /// Test exactly as an ordinary property does, and the key and the path say nothing either: without this a
    /// listing cannot report which names take Any, All and None
    /// </summary>
    [Fact]
    public void ACollectionIsToldApartFromAnOrdinaryProperty()
    {
        var bound = Boxes().WithWeequery().BindResolve().ListBindings();

        // A List<string>, a Dictionary and an int[] are bound and are not collections in this sense, an element
        // of one having no property worth naming inside a quantifier
        Assert.Equal(["Slots"], bound.Where(entry => entry.IsCollection).Select(entry => entry.Key));

        // And an element sorts directly under the collection it belongs to, the qualified key being a prefix of it
        Assert.Equal(["Id", "Labels", "Scores", "Slots", "Slots[].Label", "Slots[].Weight", "Tallies"],
            bound.Select(entry => entry.Key));

        // The one they would otherwise be indistinguishable by
        Assert.All(bound, entry => Assert.True(entry.Use.HasFlag(BindingUse.Test)));
    }

    /// <summary>
    /// What is inside a collection is an entry like any other, qualified by the collection it is reached through
    /// </summary>
    [Fact]
    public void TheInsideOfACollectionIsListedLikeEverythingElse()
    {
        var bound = Boxes().WithWeequery()
            .BindProperty(box => box.Id)
            .BindCollection(box => box.Slots!, "Slots")
            .ListBindings();

        Assert.Equal(["Id", "Slots", "Slots[].Label", "Slots[].Weight"], bound.Select(entry => entry.Key));
        Assert.Equal(["Id", "Slots", "Slots[].Label", "Slots[].Weight"], bound.Select(entry => entry.Path));
    }

    /// <summary>
    /// And it carries both of its names: the qualified one that identifies it here, and the shorter one a
    /// condition writes once the quantifier has said which collection
    /// </summary>
    [Fact]
    public void AnElementCarriesBothOfItsNames()
    {
        var bound = Boxes().WithWeequery().BindCollection(box => box.Slots!, "Slots").ListBindings();

        var weight = bound.Single(entry => entry.Key == "Slots[].Weight");

        Assert.Equal("Slots", weight.ElementOf);
        Assert.Equal("Weight", weight.ElementKey);

        // Which is the thing you actually write, the quantifier having already named the collection
        Assert.True(Boxes().WithWeequery().BindCollection(box => box.Slots!, "Slots")
            .ApplyCondition($"{weight.ElementOf} Any ({weight.ElementKey} > 10)").Validate().IsValid);

        // And the collection itself is not an element of anything
        var slots = bound.Single(entry => entry.Key == "Slots");

        Assert.Null(slots.ElementOf);
        Assert.Null(slots.ElementKey);
    }

    /// <summary>
    /// A key and a path part company inside an element exactly as they do outside one, and both are qualified
    /// </summary>
    [Fact]
    public void AnElementsKeyAndPathAreQualifiedAlike()
    {
        var bound = Boxes().WithWeequery()
            .BindCollection(box => box.Slots!, "Slots", inner => inner.BindProperty(slot => slot.Weight, "Heft"))
            .ListBindings();

        var heft = bound.Single(entry => entry.ElementOf is not null);

        Assert.Equal("Slots[].Heft", heft.Key);
        Assert.Equal("Slots[].Weight", heft.Path);
        Assert.Equal("Heft", heft.ElementKey);
    }

    /// <summary>
    /// And it reaches as far into the element as the binding did, which is the half of a resolved collection
    /// there is otherwise no way to see
    /// </summary>
    [Fact]
    public void TheInsideIsReportedAsDeepAsItWasBound()
    {
        static IEnumerable<string> Inside(Inquiry<Minion> inquiry)
        {
            return inquiry.ListBindings().Where(entry => entry.ElementOf == "LairAssignments").Select(entry => entry.ElementKey!);
        }

        Assert.Equal(["Lair", "LairID", "Minion", "MinionID"], Inside(MinionTestData.Minions().WithWeequery().BindResolve(0)));

        // The second hop of the link table, which is where the count runs away and why this is worth reading
        Assert.Contains("Lair.Name", Inside(MinionTestData.Minions().WithWeequery().BindResolve()));
    }

    /// <summary>
    /// The worked example, end to end: a property two hops down, on the far side of a collection, named the one
    /// way in the listing and the other way inside the quantifier, and both of them reported
    /// </summary>
    [Fact]
    public void AnElementTwoHopsDownReadsAsOneKey()
    {
        var inquiry = MinionTestData.Minions().WithWeequery().BindResolve();

        var name = inquiry.ListBindings().Single(entry => entry.Key == "LairAssignments[].Lair.Name");

        Assert.Equal("LairAssignments", name.ElementOf);
        Assert.Equal("Lair.Name", name.ElementKey);
        Assert.Equal(BindingUse.Test, name.Use);
        Assert.False(name.IsCollection);

        // The short name is what the condition writes, and it is the one the listing hands you for the purpose
        Assert.True(inquiry.ApplyCondition($"{name.ElementOf} Any ({name.ElementKey} = 'Volcano')").Validate().IsValid);

        // And the qualified one is not a field of the query: it says where the key lives, not how to write it.
        // The parser says so itself, the empty brackets being where an index was expected, which is the whole
        // reason the marker is spelled this way
        var refused = Assert.Throws<WeequeryException>(() => inquiry.ApplyCondition($"{name.Key} = 'Volcano'"));

        Assert.Contains("index", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(name.ElementOf!, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An element is tested and never sorted on or read back, whatever grant the binding inside happens to carry
    /// </summary>
    [Fact]
    public void TheInsideIsReportedAsTestableOnly()
    {
        var elements = Boxes().WithWeequery().BindCollection(box => box.Slots!, "Slots")
            .ListBindings().Where(entry => entry.ElementOf is not null).ToList();

        Assert.NotEmpty(elements);
        Assert.All(elements, entry => Assert.Equal(BindingUse.Test, entry.Use));

        // One level only: an inner set binds properties and cannot bind another collection
        Assert.All(elements, entry => Assert.False(entry.IsCollection));
    }

    /// <summary>
    /// The marker earns its spelling: put an index where it stands and the Path is one that binds, which is how
    /// you would reach that element by hand. It is the Path and not the Key, a key being free to differ from the
    /// property it names.
    /// </summary>
    [Fact]
    public void AnIndexSubstitutedIntoTheMarkerGivesAPathThatBinds()
    {
        var inquiry = MinionTestData.Minions().WithWeequery().BindResolve();

        var elements = inquiry.ListBindings().Where(entry => entry.ElementOf is not null).ToList();

        Assert.NotEmpty(elements);

        foreach (var element in elements)
        {
            var path = element.Path.Replace(BoundBinding.ElementMarker, "[0]", StringComparison.Ordinal);

            // Binds without throwing, which is the whole of the claim the brackets make
            var bound = MinionTestData.Minions().WithWeequery().BindProperty(path, "Probe");

            Assert.Equal(path, Assert.Single(bound.ListBindings()).Path);
        }
    }

    // ---------- the listing itself ----------

    [Fact]
    public void ItIsOrderedByKey()
    {
        var bound = Boxes().WithWeequery()
            .BindProperty(box => box.Scores)
            .BindProperty(box => box.Id)
            .BindCollection(box => box.Slots!, "Slots")
            .BindProperty(box => box.Labels)
            .ListBindings();

        Assert.Equal(["Id", "Labels", "Scores", "Slots", "Slots[].Label", "Slots[].Weight"],
            bound.Select(entry => entry.Key));
    }

    [Fact]
    public void NothingBoundListsNothing()
    {
        Assert.Empty(Boxes().WithWeequery().ListBindings());
    }

    /// <summary>
    /// The list describes the Inquiry it was asked of, and binding gives a copy, so it does not move underneath
    /// a caller holding it
    /// </summary>
    [Fact]
    public void AListingDoesNotFollowLaterBinding()
    {
        var inquiry = Boxes().WithWeequery().BindProperty(box => box.Id);
        var bound = inquiry.ListBindings();

        var wider = inquiry.BindProperty(box => box.Labels);

        Assert.Equal(["Id"], bound.Select(entry => entry.Key));
        Assert.Equal(["Id", "Labels"], wider.ListBindings().Select(entry => entry.Key));
    }

    [Fact]
    public void ItPrintsTheKeyThePathAndTheUse()
    {
        var bound = Boxes().WithWeequery()
            .BindProperty(box => box.Id)
            .BindProperty(box => box.Labels, "Tags", BindingUse.Projection)
            .ListBindings();

        Assert.Equal("'Id' may be used for all", bound[0].ToString());
        Assert.Equal("'Tags', which is Labels, may be used for projection", bound[1].ToString());

        var withCollection = Boxes().WithWeequery().BindCollection(box => box.Slots!, "Slots").ListBindings();

        Assert.Equal("'Slots', a collection, may be used for test", withCollection[0].ToString());

        Assert.Equal("'Slots[].Label', written 'Label' inside Slots, may be used for test",
            withCollection[1].ToString());
    }
}
