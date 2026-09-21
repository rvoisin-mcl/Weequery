using Tests.Common;
using Weequery;

namespace Tests.Unit;

// ---------- types that exist only to be walked ----------

/// <summary>
/// Refers to itself twice over, which is the shape that has no bottom. Without a cycle guard the walk is stopped
/// only by the depth limit, and every level multiplies the paths rather than adding to them.
/// </summary>
public class Node
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
    public Node? Parent { get; set; }
    public Node? Peer { get; set; }
}

/// <summary>Two types that refer to each other, so the cycle is longer than one hop</summary>
public class Ping
{
    public int Id { get; set; }
    public Pong? Pong { get; set; }
}

public class Pong
{
    public int Id { get; set; }
    public Ping? Ping { get; set; }
}

/// <summary>The same type reached twice down different branches, which is not a cycle and must still expand</summary>
public class Envelope
{
    public Corner? Left { get; set; }
    public Corner? Right { get; set; }
}

public class Corner
{
    public int X { get; set; }
    public int Y { get; set; }
}

/// <summary>Properties named after operators, which cannot be keyed as they stand</summary>
public class Awkward
{
    public int Id { get; set; }
    public string Contains { get; set; } = "";
    public string And { get; set; } = "";
    public Corner? OrderBy { get; set; }
}

/// <summary>A nested property named after an operator, which is not a collision at all</summary>
public class Outer
{
    public int Id { get; set; }
    public Awkward? Inner { get; set; }
}

// ---------- the shapes the walk has to decide about ----------

/// <summary>A struct with properties of its own, and no builder, so it cannot be bound at all</summary>
public struct Coord
{
    public int X { get; set; }
    public int Y { get; set; }
}

/// <summary>The modern spelling of the same thing, which is still a struct</summary>
public readonly record struct Money(decimal Amount, string Currency);

public interface INamed
{
    string DisplayName { get; }
}

/// <summary>Inherits its members, which reflection does not report on the derived interface</summary>
public interface IPlace : INamed
{
    int Capacity { get; }
}

public class Venue : IPlace
{
    public string DisplayName { get; set; } = "";
    public int Capacity { get; set; }
}

/// <summary>One of everything the walk treats differently</summary>
public class Assorted
{
    public int Id { get; set; }
    public Coord Where { get; set; }                       // unbindable struct
    public Coord? MaybeWhere { get; set; }                 // and its Nullable
    public Money Price { get; set; }                       // unbindable readonly record struct
    public DateTime When { get; set; }                     // bindable struct
    public DateTime? MaybeWhen { get; set; }
    public DayOfWeek Day { get; set; }                     // enum
    public DayOfWeek? MaybeDay { get; set; }
    public string Name { get; set; } = "";                 // container, of a sort
    public byte[]? Blob { get; set; }                      // array
    public List<Corner>? Corners { get; set; }             // list
    public Dictionary<string, int>? Tallies { get; set; }  // dictionary
    public IPlace? Place { get; set; }                     // interface, with an inherited member
    public Corner? Corner { get; set; }                    // an ordinary class, to prove the rest still expands
}

/// <summary>A class with an indexer that is not a container, so the indexer rule has something to catch</summary>
public class Indexed
{
    public int Count { get; set; }
    public string this[int position] => position.ToString();
}

public class HasIndexed
{
    public int Id { get; set; }
    public Indexed? Thing { get; set; }
}

/// <summary>
/// Resolving bindings from a type, which is the allow-list saying yes to everything and therefore the one
/// feature here where the failure mode is exposure rather than an exception.
/// <para>
/// The walk is bounded by two different things and it is worth keeping them apart: the cycle guard bounds depth,
/// by refusing to enter a type already open on the path, and maxDepth bounds how far a model that does not cycle
/// is followed. Neither bounds width.
/// </para>
/// </summary>
public class BindingResolutionTests
{
    private static string[] Paths<TEntity>(int maxDepth, BindingResolutionSettings? settings = null) where TEntity : class
    {
        return [.. Inquiry<TEntity>.ResolveBindables(maxDepth, settings).Select(request => request.PropertyPath)];
    }

    private static string[] Keys<TEntity>(int maxDepth, BindingResolutionSettings? settings = null) where TEntity : class
    {
        return [.. Inquiry<TEntity>.ResolveBindables(maxDepth, settings).Select(request => request.Key)];
    }

    // ---------- the cycle guard ----------

    /// <summary>
    /// The one that matters. Before the guard this produced 524,284 requests at the old default depth of 16, and
    /// binding them took nine seconds and most of two gigabytes, per Inquiry, which is once per request in the
    /// shape the README recommends.
    /// </summary>
    [Fact]
    public void ASelfReferencingTypeTerminatesInsteadOfMultiplying()
    {
        // Every depth gives the same answer, because there is nowhere to go: both references are back to Node
        foreach (var depth in new[] { 1, 2, 8, 16 })
        {
            Assert.Equal(["Id", "Label", "Parent", "Peer"], Paths<Node>(depth).Order());
        }
    }

    /// <summary>
    /// The regression this whole guard exists for, stated as a cost rather than as a shape.
    /// <para>
    /// Without it, Node at the old default depth of 16 resolved 524,284 requests, and binding them took roughly
    /// nine seconds and one and a half gigabytes. Per Inquiry, which is once per request in the shape the README
    /// recommends. A bound on the count catches a guard that is removed or narrowed later, where a bound on the
    /// paths would only catch the exact shape written above.
    /// </para>
    /// </summary>
    [Fact]
    public void ACyclingModelCostsNothingToResolveAtAnyDepth()
    {
        // Comfortably above the four this produces and comfortably below anything a runaway walk reaches
        Assert.InRange(Inquiry<Node>.ResolveBindables(maxDepth: 16).Count, 1, 100);

        // and binding them is a normal amount of work, not a denial of service against the process doing it
        var bound = new List<Node>().AsQueryable().WithWeequery().BindResolve(maxDepth: 16);

        Assert.Empty(bound.ApplyCondition("Id > 0").Build().ToList());
    }

    /// <summary>
    /// The guard is on the path rather than on the type, so a cycle of two is caught as surely as a cycle of one
    /// </summary>
    [Fact]
    public void MutualReferencesTerminateToo()
    {
        // Ping opens Pong, and Pong binds its Ping back but cannot open it, exactly as Node binds Parent without
        // descending into it. The walk stops one property short of the loop rather than one property early.
        Assert.Equal(["Id", "Pong", "Pong.Id", "Pong.Ping"], Paths<Ping>(16).Order());
        Assert.Equal(["Id", "Ping", "Ping.Id", "Ping.Pong"], Paths<Pong>(16).Order());

        // and there it stops, however deep it is asked to go
        Assert.DoesNotContain("Pong.Ping.Pong", Paths<Ping>(16));
    }

    /// <summary>
    /// What the guard must not do. Two properties of one type are two paths, not a cycle, and both are expanded:
    /// a guard written as "every type seen once" would silently drop the second one.
    /// </summary>
    [Fact]
    public void TheSameTypeDownTwoBranchesIsNotACycle()
    {
        Assert.Equal(
            ["Left", "Left.X", "Left.Y", "Right", "Right.X", "Right.Y"],
            Paths<Envelope>(16).Order());
    }

    /// <summary>
    /// The property that closes the cycle is still bound, it is only not descended into. It can be compared and
    /// tested for null like any other.
    /// </summary>
    [Fact]
    public void TheCyclingPropertyIsStillBound()
    {
        Assert.Contains("Parent", Paths<Node>(16));
        Assert.DoesNotContain("Parent.Parent", Paths<Node>(16));
    }

    // ---------- the depth ----------

    [Fact]
    public void TheDefaultDepthIsOne()
    {
        Assert.Equal(Paths<Minion>(1), Paths<Minion>(maxDepth: 1));
        Assert.Equal(Inquiry<Minion>.ResolveBindables(1).Count, Inquiry<Minion>.ResolveBindables().Count);

        // and it is a real bound rather than the old limit, so one level in is more than none. Asked of a model
        // that has something to expand: Minion's only nested property is a collection, which is never entered.
        Assert.True(Paths<Envelope>(1).Length > Paths<Envelope>(0).Length);
        Assert.Equal(Paths<Envelope>(1), Paths<Envelope>(maxDepth: 1));
    }

    [Fact]
    public void DepthZeroTakesTheEntitysOwnPropertiesAndNothingElse()
    {
        Assert.DoesNotContain(Paths<Minion>(0), path => path.Contains('.'));
    }

    [Fact]
    public void DepthOneReachesOneLevelDown()
    {
        Assert.Contains("Left.X", Paths<Envelope>(1));
        Assert.DoesNotContain("Left.X", Paths<Envelope>(0));
    }

    /// <summary>
    /// Bounded rather than refused, at both ends, so a caller who asks for something silly gets the nearest
    /// sensible answer instead of an exception
    /// </summary>
    [Fact]
    public void TheDepthIsClampedRatherThanRefused()
    {
        Assert.Equal(Paths<Minion>(0), Paths<Minion>(-5));
        Assert.Equal(Paths<Minion>(16), Paths<Minion>(9999));
    }

    // ---------- keys for properties the language has claimed ----------

    /// <summary>
    /// A property called Contains makes a key a query could not tell from the operator. Refusing it would refuse
    /// the whole model for one property name, so it takes an underscore instead.
    /// </summary>
    [Fact]
    public void APropertyNamedAfterAnOperatorIsKeyedWithAnUnderscore()
    {
        var requests = Inquiry<Awkward>.ResolveBindables(0);

        Assert.Equal(["And_", "Contains_", "Id", "OrderBy_"], requests.Select(request => request.Key).Order());

        // the path is untouched, since it is the property that has to be found
        Assert.Equal(["And", "Contains", "Id", "OrderBy"], requests.Select(request => request.PropertyPath).Order());
    }

    [Fact]
    public void AnUnderscoredKeyIsAValidBindingKey()
    {
        foreach (var key in Keys<Awkward>(0))
        {
            Assert.True(WeequeryException.IsBindingKey(key), $"'{key}' should be usable as a key");
        }
    }

    /// <summary>
    /// Only a whole key can collide, since the tokenizer reads a dotted path as one word: "Inner.Contains" was
    /// never the operator, so it is left alone
    /// </summary>
    [Fact]
    public void ANestedPropertyNamedAfterAnOperatorIsNotSuffixed()
    {
        var keys = Keys<Outer>(1);

        Assert.Contains("Inner.Contains", keys);
        Assert.DoesNotContain("Inner.Contains_", keys);
    }

    /// <summary>
    /// The point of the suffix: the model binds, and the awkward property answers under its new name
    /// </summary>
    [Fact]
    public void AnAwkwardModelBindsAndTheSuffixedKeyIsUsable()
    {
        var rows = new List<Awkward> { new() { Id = 1, Contains = "yes" }, new() { Id = 2, Contains = "no" } };

        var matched = rows.AsQueryable()
            .WithWeequery()
            .BindResolve(maxDepth: 0)
            .ApplyCondition("Contains_ = 'yes'")
            .Build()
            .ToList();

        Assert.Equal(1, Assert.Single(matched).Id);
    }

    // ---------- what the walk takes and leaves ----------

    /// <summary>
    /// An indexer has no path to bind, so taking one would refuse the whole model when it came to be bound.
    /// Asked of a class that is not a container, since a container is not entered at all.
    /// </summary>
    [Fact]
    public void IndexersAreNotResolved()
    {
        var paths = Paths<HasIndexed>(2);

        Assert.Equal(["Id", "Thing", "Thing.Count"], paths.Order());
        Assert.DoesNotContain(paths, path => path.EndsWith(".Item", StringComparison.Ordinal));
    }

    // ---------- structs, and the rest of what IsClass decides ----------

    /// <summary>
    /// A struct is a leaf: bound, and never walked into. Every spelling of one, so that this is the rule rather
    /// than DateTime happening to behave.
    /// </summary>
    [Theory]
    [InlineData("When")]        // framework struct
    [InlineData("MaybeWhen")]   // and its Nullable
    [InlineData("Day")]         // enum
    [InlineData("MaybeDay")]    // and its Nullable
    public void AStructIsBoundAndNotDescendedInto(string path)
    {
        var paths = Paths<Assorted>(3);

        // the positive half matters as much as the negative: the property itself is still a key
        Assert.Contains(path, paths);
        Assert.DoesNotContain(paths, resolved => resolved.StartsWith($"{path}.", StringComparison.Ordinal));
    }

    /// <summary>
    /// The one that would go wrong quietly. A Nullable exposes Value and HasValue, and the path resolver reads
    /// both of those as members of the Nullable itself, so if the walk ever entered one they would bind and work
    /// rather than fail: a redundant key on the wire that nothing complains about.
    /// </summary>
    [Fact]
    public void ANullableNeverLeaksValueOrHasValue()
    {
        var paths = Paths<Assorted>(3);

        Assert.DoesNotContain(paths, path => path.EndsWith(".Value", StringComparison.Ordinal));
        Assert.DoesNotContain(paths, path => path.EndsWith(".HasValue", StringComparison.Ordinal));
    }

    /// <summary>
    /// Reaching into a struct is still possible, it just has to be asked for. The two coexist on one Inquiry,
    /// which is what makes the auto-bound set a starting point rather than a ceiling.
    /// </summary>
    [Fact]
    public void AStructCanStillBeReachedIntoByHand()
    {
        var names = MinionTestData.Minions()
            .WithWeequery()
            .BindResolve()
            .BindProperty("HireDate.Year", "HireYear")
            .ApplyCondition("HireYear = 2025")
            .Build()
            .ToList()
            .Select(minion => minion.Name.Split(' ')[0]);

        Assert.Equal(["David"], names);
    }

    /// <summary>
    /// A struct with no builder cannot be bound at all, unlike an unsupported reference type, which binds as an
    /// object and answers the null tests. Skipped rather than thrown, the way an indexer is: one property of a
    /// money type nobody meant to filter on should not refuse the model.
    /// </summary>
    [Theory]
    [InlineData("Where")]        // a plain struct
    [InlineData("MaybeWhere")]   // and its Nullable
    [InlineData("Price")]        // a readonly record struct
    public void AStructThatCannotBeBoundIsSkippedRatherThanRefused(string path)
    {
        Assert.DoesNotContain(path, Paths<Assorted>(3));
    }

    [Fact]
    public void AModelHoldingAnUnbindableStructStillResolvesAndBinds()
    {
        var bound = new List<Assorted>().AsQueryable().WithWeequery().BindResolve();

        Assert.Empty(bound.ApplyCondition("Id > 0").Build().ToList());
    }

    // ---------- containers ----------

    /// <summary>
    /// What is inside a container is not reachable from a filter, so all expansion yields is the container's own
    /// bookkeeping. An array used to resolve seven keys this way, Rank and SyncRoot among them.
    /// </summary>
    [Theory]
    [InlineData("Blob")]      // array
    [InlineData("Corners")]   // list
    [InlineData("Tallies")]   // dictionary
    [InlineData("Name")]      // string, which is one of these too
    public void AContainerIsBoundAndNotDescendedInto(string path)
    {
        var paths = Paths<Assorted>(3);

        Assert.Contains(path, paths);
        Assert.DoesNotContain(paths, resolved => resolved.StartsWith($"{path}.", StringComparison.Ordinal));
    }

    /// <summary>
    /// Not a setting, so there is no way to ask for it back. A caller who wants Count can bind it by hand and
    /// find out from their provider whether it translates.
    /// </summary>
    [Fact]
    public void NoSettingBringsAContainersInsidesBack()
    {
        var nothingSubtracted = new BindingResolutionSettings(new HashSet<string>(StringComparer.OrdinalIgnoreCase), [], false, []);

        Assert.DoesNotContain("Corners.Count", Paths<Assorted>(3, nothingSubtracted));
        Assert.DoesNotContain("Blob.Length", Paths<Assorted>(3, nothingSubtracted));
    }

    // ---------- interfaces ----------

    /// <summary>
    /// An interface is not a class, so the struct rule caught it by accident and a model that navigates by one
    /// resolved nothing below it. What an interface promises is reachable through it, so it is walked.
    /// </summary>
    [Fact]
    public void AnInterfaceIsDescendedInto()
    {
        Assert.Contains("Place.Capacity", Paths<Assorted>(1));
    }

    /// <summary>
    /// GetProperties on an interface reports what that interface declares and nothing it inherits, so an
    /// interface built on another would otherwise lose everything above it
    /// </summary>
    [Fact]
    public void AnInheritedInterfaceMemberIsResolvedToo()
    {
        Assert.Contains("Place.DisplayName", Paths<Assorted>(1));
    }

    [Fact]
    public void AnInterfaceTypedPropertyBindsAndFilters()
    {
        var rows = new List<Assorted>
        {
            new() { Id = 1, Place = new Venue { DisplayName = "Volcano", Capacity = 40 } },
            new() { Id = 2, Place = new Venue { DisplayName = "Bunker", Capacity = 10 } },
        }.AsQueryable();

        var matched = rows.WithWeequery().BindResolve().ApplyCondition("Place.Capacity > 20").Build().ToList();

        Assert.Equal(1, Assert.Single(matched).Id);
    }

    /// <summary>
    /// An interface that is a container is still a container, so the two rules compose rather than fight
    /// </summary>
    [Fact]
    public void AnInterfaceThatIsAContainerIsStillNotDescendedInto()
    {
        Assert.DoesNotContain(Paths<Assorted>(3), path => path.StartsWith("Corners.", StringComparison.Ordinal));
    }

    /// <summary>
    /// A string is a class, so the walk would otherwise resolve a Length for every string property in the model.
    /// It is a container as well, and that rule is not a setting: there is no way to ask for Name.Length.
    /// </summary>
    [Fact]
    public void AStringIsNeverExpanded()
    {
        Assert.DoesNotContain("Name.Length", Paths<Minion>(2));

        // and it stays out with every subtraction cleared, which is what makes it a rule rather than a default
        var nothingSubtracted = new BindingResolutionSettings(new HashSet<string>(StringComparer.OrdinalIgnoreCase), [], false, []);

        Assert.DoesNotContain("Name.Length", Paths<Minion>(2, nothingSubtracted));
        Assert.DoesNotContain("Name.Chars", Paths<Minion>(2, nothingSubtracted));
    }

    [Fact]
    public void StandardIsReachableAndComposesWithAWithExpression()
    {
        var settings = BindingResolutionSettings.Default with
        {
            IgnorePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Pay" },
        };

        var paths = Paths<Minion>(1, settings);

        Assert.DoesNotContain("Pay", paths);
        Assert.DoesNotContain("Name.Length", paths);   // the string rule survived the copy
    }

    // ---------- the subtractions ----------

    [Fact]
    public void AnIgnoredPathIsNotBoundAndNotDescendedInto()
    {
        var settings = BindingResolutionSettings.Default with
        {
            IgnorePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Left" },
        };

        var paths = Paths<Envelope>(1, settings);

        Assert.DoesNotContain("Left", paths);
        Assert.DoesNotContain("Left.X", paths);
        Assert.Contains("Right.X", paths);
    }

    /// <summary>
    /// The trailing period is the difference between leaving a property out and stopping at it, which is the one
    /// piece of this that a reader would not guess
    /// </summary>
    [Fact]
    public void ATrailingPeriodBindsThePropertyButStopsThere()
    {
        var settings = BindingResolutionSettings.Default with
        {
            IgnorePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Left." },
        };

        var paths = Paths<Envelope>(1, settings);

        Assert.Contains("Left", paths);
        Assert.DoesNotContain("Left.X", paths);
    }

    [Fact]
    public void IgnorePathsAreMatchedWithoutRegardToCase()
    {
        // Given with the default comparer, so the copy constructor is what has to fix it
        var settings = BindingResolutionSettings.Default with { IgnorePaths = ["lEfT"] };

        Assert.DoesNotContain("Left", Paths<Envelope>(1, settings));
    }

    [Fact]
    public void AnIgnoredTypeIsLeftOutWhereverItAppears()
    {
        var settings = BindingResolutionSettings.Default with { IgnoreTypes = [typeof(Corner)] };

        var paths = Paths<Envelope>(1, settings);

        Assert.Empty(paths);
    }

    [Fact]
    public void AnIgnoredTypeCanCatchWhatDerivesFromItWhenAsked()
    {
        var exact = BindingResolutionSettings.Default with { IgnoreTypes = [typeof(object)] };
        var assignable = BindingResolutionSettings.Default with
        {
            IgnoreTypes = [typeof(object)],
            IgnoreTypeWhenAssignable = true,
        };

        // Nothing is declared as object, so matching exactly leaves everything in
        Assert.NotEmpty(Paths<Envelope>(1, exact));

        // and everything is assignable to it, so the other way leaves nothing
        Assert.Empty(Paths<Envelope>(1, assignable));
    }

    // ---------- binding what was resolved ----------

    [Fact]
    public void BindResolveBindsWhatResolutionFoundAndTheQueryRuns()
    {
        var names = MinionTestData.Minions()
            .WithWeequery()
            .BindResolve()
            .ApplyCondition("Pay > 10000")
            .ApplySorts("Pay DESC")
            .Build()
            .ToList()
            .Select(minion => minion.Name.Split(' ')[0])
            .ToArray();

        Assert.Equal(["Charlie", "Alice"], names);
    }

    [Fact]
    public void BindResolveReachesANestedKeyByItsDottedPath()
    {
        var rows = new List<Envelope>
        {
            new() { Left = new Corner { X = 1 }, Right = new Corner { X = 9 } },
            new() { Left = new Corner { X = 5 }, Right = new Corner { X = 9 } },
        };

        var matched = rows.AsQueryable()
            .WithWeequery()
            .BindResolve()
            .ApplyCondition("Left.X > 3")
            .Build()
            .ToList();

        Assert.Equal(5, Assert.Single(matched).Left!.X);
    }

    /// <summary>
    /// Resolving adds rather than replaces, so a key already claimed by hand for the same property is that one
    /// binding rather than a collision, and the hand bound one is the one kept: it may carry a narrower use or a
    /// converter the resolver knows nothing about, and the resolver must not quietly widen it.
    /// </summary>
    [Fact]
    public void BindResolveKeepsTheBindingAlreadyMadeByHand()
    {
        // an Inquiry accumulates its conditions, so the two checks below need one each
        static Inquiry<Minion> Bound() => MinionTestData.Minions()
            .WithWeequery()
            .BindProperty(minion => minion.Pay, use: BindingUse.Projection)
            .BindResolve();

        // the hand bound Pay survived, so it is still projection only and a condition on it is refused
        Assert.Throws<WeequeryException>(() => Bound().ApplyCondition("Pay > 1").Build().ToList());

        // and the rest of the properties resolved around it
        Assert.NotEmpty(Bound().ApplyCondition("Name != ''").Build().ToList());
    }

    [Fact]
    public void ResolveThenRemoveIsTheWayToSubtractAfterTheFact()
    {
        var inquiry = MinionTestData.Minions()
            .WithWeequery()
            .BindResolve(maxDepth: 0)
            .RemoveBinding("Pay");

        // the removed key is no longer answerable, and the others still are
        Assert.Throws<WeequeryException>(() => inquiry.ApplyCondition("Pay > 1").Build().ToList());
        Assert.NotEmpty(MinionTestData.Minions().WithWeequery().BindResolve(maxDepth: 0).ApplyCondition("Pay > 1").Build().ToList());
    }

    [Fact]
    public void RemovingAKeyThatWasNeverBoundIsANoOp()
    {
        var inquiry = MinionTestData.Minions().WithWeequery().BindResolve(maxDepth: 0);

        Assert.NotEmpty(inquiry.RemoveBinding("NotBoundAtAll").ApplyCondition("Pay > 1").Build().ToList());
    }

    [Fact]
    public void RemoveBindingMatchesTheKeyWithoutRegardToCase()
    {
        var inquiry = MinionTestData.Minions().WithWeequery().BindResolve(maxDepth: 0).RemoveBinding("pAy");

        Assert.Throws<WeequeryException>(() => inquiry.ApplyCondition("Pay > 1").Build().ToList());
    }

    // ---------- the shape of what comes back ----------

    [Fact]
    public void EveryResolvedKeyIsUsableAsOne()
    {
        foreach (var key in Keys<Minion>(2))
        {
            Assert.True(WeequeryException.IsBindingKey(key), $"'{key}' should be usable as a key");
        }
    }

    [Fact]
    public void ResolutionIsStableFromOneCallToTheNext()
    {
        // Reflection does not promise an order, so the walk sorts; a set that shifted between runs would make
        // the keys a caller sees depend on nothing they can see
        Assert.Equal(Paths<Minion>(2), Paths<Minion>(2));
        Assert.Equal(Paths<Minion>(2).Order(), Paths<Minion>(2));
    }
}
