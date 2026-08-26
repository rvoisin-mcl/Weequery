using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// A property can be bound without being sortable: an unsupported reference type binds so that it can be tested
/// for null, and a collection or a navigation property has no ordering of its own.
/// <para>
/// The sort methods fall back to Comparer&lt;T&gt;.Default, which only complains when it is actually asked to
/// compare two such values, so the same sort worked or failed on the shape of the data: one row with a value
/// sorted happily, and a second one brought down the enumeration.
/// </para>
/// </summary>
public class SortableFieldTests
{
    private class Thing
    {
        public string Name { get; set; } = "";
        public List<string>? Tags { get; set; }
        public Version? Version { get; set; }
    }

    private static IQueryable<Thing> Things()
    {
        return new List<Thing>
        {
            new() { Name = "a", Tags = ["x"], Version = new Version(2, 0) },
            new() { Name = "b", Tags = ["y"], Version = new Version(1, 0) },
        }.AsQueryable();
    }

    [Fact]
    public void SortingOnAPropertyWithNoOrderingIsRefused()
    {
        Assert.Throws<WeequeryException>(() => Things()
            .WithWeequery()
            .BindProperty(thing => thing.Tags, "Tags")
            .ApplySort(new Sort("Tags", SortDirection.Ascending))
            .Build());
    }

    /// <summary>
    /// It is refused whatever the data, where before it depended on how many rows had a value
    /// </summary>
    [Fact]
    public void ItIsRefusedEvenWhenTheDataWouldNotHaveNoticed()
    {
        var single = new List<Thing> { new() { Tags = ["x"] } }.AsQueryable();

        Assert.Throws<WeequeryException>(() => single
            .WithWeequery()
            .BindProperty(thing => thing.Tags, "Tags")
            .ApplySort(new Sort("Tags", SortDirection.Ascending))
            .Build());
    }

    /// <summary>
    /// Binding it is still fine, and so is asking whether it is there: only the ordering is refused
    /// </summary>
    [Fact]
    public void TheSamePropertyCanStillBeTestedForNull()
    {
        var rows = new List<Thing> { new() { Tags = ["x"] }, new() { Tags = null } }.AsQueryable();

        Assert.Equal(1, rows.WithWeequery().BindProperty(thing => thing.Tags, "Tags").ApplyCondition("Tags IsNull").Build().Count());
    }

    /// <summary>
    /// A type that can order itself is not caught by the check, even though Weequery has no builder for it: the
    /// question is whether it can be compared, not whether it can be filtered on
    /// </summary>
    [Fact]
    public void ATypeThatCanOrderItselfIsStillSortable()
    {
        var sorted = Things()
            .WithWeequery()
            .BindProperty(thing => thing.Version, "Version")
            .ApplySort(new Sort("Version", SortDirection.Ascending))
            .Build()
            .ToList();

        Assert.Equal(["b", "a"], sorted.Select(thing => thing.Name));
    }

    /// <summary>
    /// The ordinary properties are unaffected, including the nullable ones
    /// </summary>
    [Theory]
    [InlineData(nameof(Minion.Pay))]
    [InlineData(nameof(Minion.Name))]
    [InlineData(nameof(Minion.Classification))]
    [InlineData(nameof(Minion.IsVetted))]
    [InlineData(nameof(Minion.ReviewDate))]
    [InlineData(nameof(Minion.MinionID))]
    public void TheSupportedTypesAreAllSortable(string field)
    {
        var sorted = MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplySort(new Sort(field, SortDirection.Ascending))
            .Build()
            .ToList();

        Assert.Equal(4, sorted.Count);
    }
}
