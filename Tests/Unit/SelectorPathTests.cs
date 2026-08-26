using System.Linq.Expressions;
using Tests.Common;
using Weequery;
using Weequery.Interfaces;

namespace Tests.Unit;

/// <summary>
/// BindProperty(selector) has to work out which property a lambda points at. It used to read that out of the
/// lambda's text, slicing at the first '.' and the first ',' to get past the conversions the compiler inserts;
/// it now walks the member chain instead.
/// <para>
/// These cover the shapes that made the text approach delicate a conversion around the member, a path several
/// members long, an explicit .Value and the shapes that are not property paths at all, which used to fail with
/// a message about whatever the slicing happened to produce.
/// </para>
/// </summary>
public class SelectorPathTests
{
    private static IQueryable<Minion> Minions()
    {
        return MinionTestData.Minions();
    }

    /// <summary>
    /// Bind by selector, then filter on the key it derived, so the test proves the path rather than just that
    /// nothing threw
    /// </summary>
    private static int CountWhere<TProperty>(Expression<Func<Minion, TProperty>> selector, string key, ICondition condition)
    {
        return Minions()
            .WithWeequery()
            .BindProperty(selector, key)
            .ApplyCondition(condition)
            .Build()
            .Count();
    }

    [Fact]
    public void APlainPropertyBindsUnderItsOwnName()
    {
        var result = Minions()
            .WithWeequery()
            .BindProperty(minion => minion.Pay)
            .ApplyCondition("Pay > 10000")
            .Build()
            .Count();

        Assert.Equal(2, result);
    }

    /// <summary>
    /// The awkward case for reading the text: selecting a value type as an object wraps the member in a
    /// Convert, which prints as "Convert(minion.Pay, Object)" a comma and a second dot that are not the path
    /// </summary>
    [Fact]
    public void APropertyBoxedByTheSelectorStillGivesItsOwnPath()
    {
        Assert.Equal(2, CountWhere<object>(minion => minion.Pay, "Pay", new OneValueCondition<decimal>(Operator.GreaterThan, "Pay", 10000m)));
    }

    /// <summary>
    /// The same, where the conversion is to a generic type, so the printed form carries a backtick and a comma
    /// </summary>
    [Fact]
    public void APropertyWidenedToAGenericTypeStillGivesItsOwnPath()
    {
        var binding = Minions()
            .WithWeequery()
            .BindProperty<IEnumerable<LairAssignment>?>(minion => minion.LairAssignments, "Assignments");

        // An unsupported reference type binds, and supports the null tests, which is enough to show the path took
        Assert.Equal(4, binding.ApplyCondition("Assignments IsNull").Build().Count());
    }

    [Fact]
    public void ANestedPathKeepsAllItsSegments()
    {
        var lairs = new List<LairAssignment>
        {
            new() { Lair = new Lair { Name = "Volcano", Capacity = 10 } },
            new() { Lair = new Lair { Name = "Bunker", Capacity = 2 } },
        }.AsQueryable();

        var result = lairs
            .WithWeequery()
            .BindProperty(assignment => assignment.Lair!.Capacity, "LairCapacity")
            .ApplyCondition("LairCapacity > 5")
            .Build()
            .Count();

        Assert.Equal(1, result);
    }

    /// <summary>
    /// An explicit .Value is a member of the Nullable itself, so it belongs in the path and the binding resolves
    /// it as written
    /// </summary>
    [Fact]
    public void AnExplicitValueStaysInThePath()
    {
        Assert.Equal(2, CountWhere(minion => minion.BirthDate!.Value.Year, "BirthYear", new OneValueCondition<int>(Operator.LessThan, "BirthYear", 2000)));
    }

    /// <summary>
    /// A selector can only reach a member of the underlying type by naming Value, since a Nullable does not expose
    /// the members of what it wraps. A path that names Value unwraps rather than reaching through, so the binding
    /// is the int and the null tests have nothing to ask about it; the string form, "ReviewDate.Year", is the one
    /// that reaches through and stays nullable. Both were already true, and the path a selector gives has not
    /// changed only how it is read off the lambda.
    /// </summary>
    [Fact]
    public void ASelectorNamingValueUnwrapsWhereAStringPathReachesThrough()
    {
        var unwrapped = Minions().WithWeequery().BindProperty(minion => minion.ReviewDate!.Value.Year, "ReviewYear");

        Assert.Throws<WeequeryException>(() => unwrapped.ApplyCondition(new NoValueCondition(Operator.IsNull, "ReviewYear")).Build());

        var reachedThrough = Minions().WithWeequery().BindProperty("ReviewDate.Year", "ReviewYear");

        // Bob was never reviewed, so his ReviewYear has no value either
        Assert.Equal(1, reachedThrough.ApplyCondition(new NoValueCondition(Operator.IsNull, "ReviewYear")).Build().Count());
    }

    // ---------- a selector plus the segments it cannot write ----------

    /// <summary>
    /// The whole point of the overload: "BirthDate.Year" reaches through the nullable, so the bound member is
    /// nullable in its own right, and a selector cannot say that on its own. Same binding the string path gives,
    /// with the compiler still checking that BirthDate exists.
    /// </summary>
    [Fact]
    public void ASelectorWithSegmentsReachesThroughANullable()
    {
        var inquiry = Minions().WithWeequery().BindProperty(minion => minion.ReviewDate, ["Year"], "ReviewYear");

        // Bob was never reviewed, so his ReviewYear has no value: the null tests apply, which they do not to the
        // plain int an explicit .Value would have bound
        Assert.Equal(1, inquiry.ApplyCondition("ReviewYear IsNull").Build().Count());
    }

    [Fact]
    public void ASelectorWithSegmentsMatchesTheEquivalentStringPath()
    {
        foreach (var query in new[] { "BirthYear IsNull", "BirthYear < 2000", "BirthYear IsNotNull" })
        {
            var bySegments = Minions().WithWeequery().BindProperty(minion => minion.BirthDate, ["Year"], "BirthYear").ApplyCondition(query).Build().Count();
            var byPath = Minions().WithWeequery().BindProperty("BirthDate.Year", "BirthYear").ApplyCondition(query).Build().Count();

            Assert.Equal(byPath, bySegments);
        }
    }

    /// <summary>
    /// Not only for nullables: it names any path the caller would rather not spell as a string
    /// </summary>
    [Fact]
    public void ASelectorWithSegmentsAlsoWalksPlainMembers()
    {
        var result = Minions()
            .WithWeequery()
            .BindProperty(minion => minion.HireDate, ["Year"], "HireYear")
            .ApplyCondition("HireYear == 2025")
            .Build()
            .Count();

        Assert.Equal(1, result);
    }

    [Fact]
    public void SeveralSegmentsFollowInOrder()
    {
        var assignments = new List<LairAssignment>
        {
            new() { Lair = new Lair { Name = "Volcano", Capacity = 10 } },
            new() { Lair = new Lair { Name = "Bunker", Capacity = 2 } },
        }.AsQueryable();

        var result = assignments
            .WithWeequery()
            .BindProperty(assignment => assignment.Lair, ["Name", "Length"], "LairNameLength")
            .ApplyCondition("LairNameLength > 6")
            .Build()
            .Count();

        Assert.Equal(1, result);
    }

    /// <summary>
    /// The whole path has periods in it, so it cannot be the key. The last segment is, matching what the segments
    /// constructor of a BindingRequest derives.
    /// </summary>
    [Fact]
    public void TheKeyDefaultsToTheLastSegment()
    {
        var result = Minions()
            .WithWeequery()
            .BindProperty(minion => minion.HireDate, ["Year"])
            .ApplyCondition("Year == 2025")
            .Build()
            .Count();

        Assert.Equal(1, result);
    }

    [Fact]
    public void SegmentsMustNameSomething()
    {
        Assert.Throws<WeequeryException>(() => Minions().WithWeequery().BindProperty(minion => minion.BirthDate, [], "BirthYear"));
        Assert.Throws<WeequeryException>(() => Minions().WithWeequery().BindProperty(minion => minion.BirthDate, [""], "BirthYear"));
        Assert.Throws<WeequeryException>(() => Minions().WithWeequery().BindProperty(minion => minion.BirthDate, null!, "BirthYear"));
    }

    [Fact]
    public void ASegmentThatIsNotAMemberIsReportedWithThePath()
    {
        Assert.Throws<WeequeryException>(() => Minions().WithWeequery().BindProperty(minion => minion.BirthDate, ["Decade"], "BirthDecade"));
    }

    /// <summary>
    /// The overload takes an array where the other takes a key, so a call meaning one cannot be read as the other
    /// </summary>
    [Fact]
    public void TheSegmentsOverloadDoesNotStealTheKeyOverload()
    {
        // "Year" here is the key for BirthDate itself, not a segment: the property bound is the DateTime?
        var inquiry = Minions().WithWeequery().BindProperty(minion => minion.BirthDate, "Year");

        Assert.Equal(4, inquiry.ApplyCondition("Year IsNotNull").Build().Count());
    }

    // ---------- selectors that are not property paths ----------

    /// <summary>
    /// Refused rather than bound to whatever the slicing produced, which for a captured variable was a fragment
    /// of a compiler generated class name
    /// </summary>
    [Fact]
    public void ASelectorReadingACapturedVariableIsRefused()
    {
        var other = new Minion { Name = "Someone Else" };

        Assert.Throws<WeequeryException>(() => Minions().WithWeequery().BindProperty(minion => other.Name, "Name"));
    }

    [Fact]
    public void ASelectorReadingAStaticIsRefused()
    {
        Assert.Throws<WeequeryException>(() => Minions().WithWeequery().BindProperty(minion => DateTime.Now.Year, "Year"));
    }

    [Fact]
    public void ASelectorThatIsNotAMemberAtAllIsRefused()
    {
        Assert.Throws<WeequeryException>(() => Minions().WithWeequery().BindProperty(minion => minion.Name.Length > 0, "Named"));
    }

    [Fact]
    public void ASelectorCallingAMethodIsRefused()
    {
        Assert.Throws<WeequeryException>(() => Minions().WithWeequery().BindProperty(minion => minion.Name.ToUpperInvariant(), "Upper"));
    }

    [Fact]
    public void ASelectorReturningTheEntityItselfIsRefused()
    {
        Assert.Throws<WeequeryException>(() => Minions().WithWeequery().BindProperty(minion => minion, "Self"));
    }

    /// <summary>
    /// The selector and the string path are two ways to say the same thing, so they must bind the same property
    /// </summary>
    [Fact]
    public void TheSelectorAndTheStringPathAgree()
    {
        var bySelector = Minions().WithWeequery().BindProperty(minion => minion.Pay, "A");
        var byPath = Minions().WithWeequery().BindProperty(nameof(Minion.Pay), "B");

        var condition = new OneValueCondition<decimal>(Operator.GreaterThan, "A", 10000m);
        var samePath = new OneValueCondition<decimal>(Operator.GreaterThan, "B", 10000m);

        Assert.Equal(bySelector.ApplyCondition(condition).Build().Count(), byPath.ApplyCondition(samePath).Build().Count());
    }
}
