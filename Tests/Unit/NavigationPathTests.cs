using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// A path may reach the property through something that is not there: a Nullable&lt;&gt; with no value, or a
/// reference that is null. The first was always guarded; the second used to throw NullReferenceException out of
/// the built expression when it ran in memory, while a database answered the same condition through the join.
/// <para>
/// Both are guarded now, so a path through a missing link matches nothing rather than failing, and IsNull on such
/// a path asks whether the link is there the same thing it already meant for a nullable.
/// </para>
/// </summary>
public class NavigationPathTests
{
    private static IQueryable<LairAssignment> Assignments()
    {
        return new List<LairAssignment>
        {
            new() { Lair = new Lair { Name = "Volcano", Capacity = 10 } },
            new() { Lair = new Lair { Name = "Bunker", Capacity = 2 } },
            new() { Lair = null },
        }.AsQueryable();
    }

    private static int Count(string key, string path, string query)
    {
        return Assignments()
            .WithWeequery()
            .BindProperty(path, key)
            .ApplyCondition(query)
            .Build()
            .Count();
    }

    /// <summary>
    /// The value type case, which used to be the crash: nothing guarded the read of Capacity through a null Lair
    /// </summary>
    [Fact]
    public void AComparisonThroughANullReferenceMatchesNothingRatherThanThrowing()
    {
        Assert.Equal(1, Count("LairCapacity", "Lair.Capacity", "LairCapacity > 5"));
        Assert.Equal(2, Count("LairCapacity", "Lair.Capacity", "LairCapacity > 1"));
    }

    [Fact]
    public void AStringComparisonThroughANullReferenceIsGuardedToo()
    {
        Assert.Equal(1, Count("LairName", "Lair.Name", "LairName == 'Volcano'"));
        Assert.Equal(0, Count("LairName", "Lair.Name", "LairName == 'Nowhere'"));
    }

    /// <summary>
    /// The negative operators keep the rule they follow everywhere else: a row with nothing to compare does not
    /// match, so it is not "not equal to" either
    /// </summary>
    [Fact]
    public void TheNegativeOperatorsDoNotCatchAMissingLink()
    {
        Assert.Equal(1, Count("LairName", "Lair.Name", "LairName != 'Volcano'"));
        Assert.Equal(1, Count("LairName", "Lair.Name", "LairName DoesNotContain 'o'"));
    }

    /// <summary>
    /// Reached through a reference, the member is nullable in its own right, exactly as one reached through a
    /// Nullable&lt;&gt; is: IsNull asks whether the link is there
    /// </summary>
    [Fact]
    public void IsNullOnAPathAsksWhetherTheLinkIsThere()
    {
        Assert.Equal(1, Count("LairCapacity", "Lair.Capacity", "LairCapacity IsNull"));
        Assert.Equal(2, Count("LairCapacity", "Lair.Capacity", "LairCapacity IsNotNull"));
    }

    /// <summary>
    /// The three buckets still partition the rows, which is the property the whole null rule is built on
    /// </summary>
    [Fact]
    public void MatchedNotMatchedAndMissingAccountForEveryRow()
    {
        var matched = Count("LairCapacity", "Lair.Capacity", "LairCapacity > 5");
        var notMatched = Count("LairCapacity", "Lair.Capacity", "LairCapacity <= 5");
        var missing = Count("LairCapacity", "Lair.Capacity", "LairCapacity IsNull");

        Assert.Equal(3, matched + notMatched + missing);
    }

    /// <summary>
    /// Sorting reads the same path, so it needed the same guard
    /// </summary>
    [Fact]
    public void SortingOnAPathThroughANullReferenceWorks()
    {
        var sorted = Assignments()
            .WithWeequery()
            .BindProperty("Lair.Capacity", "LairCapacity")
            .ApplySort(new Sort("LairCapacity", SortDirection.Descending))
            .Build()
            .ToList();

        Assert.Equal(3, sorted.Count);
        Assert.Equal(10, sorted.First().Lair!.Capacity);
    }

    /// <summary>
    /// A path of one segment gains no guard it did not have, so an ordinary property is unaffected
    /// </summary>
    [Fact]
    public void APlainPropertyIsUnaffected()
    {
        Assert.Equal(2, MinionTestData.Minions().WithWeequery().BindProperties(Minion.Bindings).ApplyCondition("Pay > 10000").Build().Count());
    }
}
