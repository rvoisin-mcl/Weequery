using Microsoft.EntityFrameworkCore;
using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// Binding to a member reached through a Nullable&lt;&gt;, which used to be impossible: a path like
/// "BirthDate.Year" on a DateTime? failed with "'Year' is not a member of type 'System.Nullable`1[DateTime]'",
/// because Nullable&lt;T&gt; exposes only its own HasValue and Value. The path builder now steps through .Value.
/// <para>
/// A member reached this way behaves as a nullable in its own right: the binding carries a guard for the link, so
/// IsNull on the derived member answers for the parent and a comparison never dereferences a null.
/// </para>
/// <para>
/// Bindings here are declared per test rather than added to Minion.Bindings, so the shared fixture stays a plain
/// one-key-per-property map.
/// </para>
/// </summary>
public class NullablePathTests
{
    /// <summary>
    /// Minion.BirthDate is DateTime? and set on every row, Minion.FireDate is DateTime? and null on three of four,
    /// Minion.ReviewDate is DateOnly? and null on one, Minion.HireDate is a plain DateTime
    /// </summary>
    private static readonly BindingRequest[] Bindings =
    [
        new(nameof(Minion.Name), null),
        new(nameof(Minion.BirthDate), null),
        new(nameof(Minion.FireDate), null),
        new(nameof(Minion.ReviewDate), null),
        new("BirthDate.Year", "BirthYear"),
        new("BirthDate.Month", "BirthMonth"),
        new("BirthDate.Value.Day", "BirthDay"),
        new("BirthDate.HasValue", "BirthKnown"),
        new("HireDate.Year", "HireYear"),
        new("FireDate.Year", "FireYear"),
        new("ReviewDate.Year", "ReviewYear"),
    ];

    private static string[] Matching(string query)
    {
        return MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Bindings)
            .ApplyCondition(query)
            .Build()
            .ToList()
            .Select(minion => minion.Name.Split(' ')[0])
            .Order()
            .ToArray();
    }

    // ---------- the binding itself ----------

    [Theory]
    [InlineData("BirthDate.Year")]
    [InlineData("BirthDate.Month")]
    [InlineData("BirthDate.Day")]
    [InlineData("BirthDate.DayOfWeek")]
    [InlineData("FireDate.Year")]
    [InlineData("ReviewDate.Year")]
    [InlineData("ReviewDate.DayOfYear")]
    public void AMemberInsideANullableCanBeBound(string path)
    {
        // Creating the binding at all is the assertion; this used to throw
        MinionTestData.Minions().WithWeequery().BindProperty(path, "Derived");
    }

    [Fact]
    public void AnExplicitValueInThePathStillWorks()
    {
        // ".Value" is a member of the Nullable itself, so it must not get a second .Value stubbed in
        Assert.Equal(["Alice"], Matching("BirthDay == 5 && BirthYear == 2000"));
    }

    [Fact]
    public void TheNullablesOwnMembersAreReachable()
    {
        // HasValue belongs to the Nullable, not to DateTime, so it resolves without unwrapping
        Assert.Equal(["Alice", "Bob", "Charlie", "David"], Matching("BirthKnown == true"));
    }

    [Fact]
    public void APlainNonNullableMemberStillWorks()
    {
        // HireDate is a plain DateTime, so this path never needed unwrapping and must be unaffected
        Assert.Equal(["Alice", "Bob", "Charlie"], Matching("HireYear == 2018"));
        Assert.Equal(["David"], Matching("HireYear == 2025"));
    }

    // ---------- filtering on the derived value ----------
    // BirthDate: Alice 2000-01-05, Bob 1990-11-05, Charlie 1984-01-05, David 2012-01-05

    [Fact]
    public void TheDerivedValueFilters()
    {
        Assert.Equal(["Alice"], Matching("BirthYear == 2000"));
        Assert.Equal(["Charlie"], Matching("BirthYear < 1990"));
        Assert.Equal(["Alice", "David"], Matching("BirthYear IsIn (2000, 2012)"));
        Assert.Equal(["Alice", "Bob"], Matching("BirthYear IsBetween (1990, 2000)"));
    }

    /// <summary>
    /// The derived member behaves as a nullable because its parent is one. Year is an int, but it has no value at
    /// all when BirthDate is null, so IsNull applies to it and answers for the link.
    /// </summary>
    [Fact]
    public void TheDerivedValueIsNullableBecauseItsParentIs()
    {
        // BirthDate is set on every row, so nothing is null here
        Assert.Empty(Matching("BirthYear IsNull"));
        Assert.Equal(["Alice", "Bob", "Charlie", "David"], Matching("BirthYear IsNotNull"));

        // FireDate is null for three of four, and the derived year reports exactly that
        Assert.Equal(["Alice", "Charlie", "David"], Matching("FireYear IsNull"));
        Assert.Equal(["Bob"], Matching("FireYear IsNotNull"));

        // and it agrees with asking the link directly
        Assert.Equal(Matching("FireDate IsNull"), Matching("FireYear IsNull"));
    }

    [Fact]
    public void MonthAndDayComeThroughToo()
    {
        Assert.Equal(["Alice", "Charlie", "David"], Matching("BirthMonth == 1"));
        Assert.Equal(["Bob"], Matching("BirthMonth == 11"));
    }

    [Fact]
    public void ADateOnlyMemberWorksTheSameWay()
    {
        // ReviewDate is DateOnly?, so this exercises a nullable of a different value type
        Assert.Equal(["Alice"], Matching("(ReviewDate IsNotNull) && (ReviewYear == 2024)"));
    }

    // ---------- the null link, now handled by the binding itself ----------

    /// <summary>
    /// A null link needs no guard from the caller. The binding carries its own, so the unwrap is never reached for
    /// a row whose link is null, and the comparison simply does not match it.
    /// </summary>
    [Fact]
    public void ANullLinkIsSafeInMemoryWithoutTheCallerGuardingIt()
    {
        // FireDate is null for Alice, Charlie and David, and this used to throw
        Assert.Equal(["Bob"], Matching("FireYear == 2024"));
        Assert.Empty(Matching("FireYear == 1999"));
    }

    /// <summary>
    /// And a null does not satisfy the negative form either, matching what a database answers
    /// </summary>
    [Fact]
    public void ANullLinkDoesNotSatisfyANegativeComparison()
    {
        Assert.Empty(Matching("FireYear != 2024"));
        Assert.Equal(["Bob"], Matching("FireYear != 1999"));
    }

    /// <summary>
    /// Guarding it explicitly is now redundant, but must still give the same answer
    /// </summary>
    [Fact]
    public void AnExplicitGuardIsRedundantButHarmless()
    {
        Assert.Equal(Matching("FireYear == 2024"), Matching("(FireDate IsNotNull) && (FireYear == 2024)"));
        Assert.Equal(["Alice", "Charlie", "David"], Matching("(FireDate IsNull) || (FireYear == 1999)"));
    }

    // ---------- a bad path reports usefully ----------

    [Fact]
    public void AnUnresolvableSegmentIsReportedAsAWeequeryException()
    {
        Assert.Throws<WeequeryException>(() => MinionTestData.Minions().WithWeequery().BindProperty("BirthDate.Nonsense", "Derived"));
    }

    [Fact]
    public void AnUnresolvableSegmentOnAPlainTypeIsAlsoReported()
    {
        Assert.Throws<WeequeryException>(() => MinionTestData.Minions().WithWeequery().BindProperty("Name.Nonsense", "Derived"));
    }

    // ---------- and it translates, where the database handles the null for us ----------

    [Theory]
    [MemberData(nameof(TestDatabase.AllProviders), MemberType = typeof(TestDatabase))]
    public void ADerivedMemberTranslates(TestProvider provider)
    {
        using var context = TestDatabase.Create(provider);

        foreach (var query in new[] { "BirthYear == 2000", "BirthMonth > 6", "FireYear == 2024", "ReviewYear == 2024" })
        {
            var sql = context.Minions
                .WithWeequery()
                .BindProperties(Bindings)
                .ApplyCondition(query)
                .Build()
                .ToQueryString();

            Assert.Contains("SELECT", sql);
        }
    }

    /// <summary>
    /// The database and memory now agree: SQL yields null for the extracted part and the comparison
    /// simply does not match, and in memory the guard short circuits to the same answer. Worth pinning, since
    /// this used to be the one place the two disagreed.
    /// </summary>
    [Fact]
    public void ANullLinkAgreesBetweenMemoryAndTheDatabase()
    {
        var context = TestDatabase.CreateSeeded(TestProvider.Sqlite);
        try
        {
            string[] Names(string query)
            {
                return context.Minions
                    .WithWeequery()
                    .BindProperties(Bindings)
                    .ApplyCondition(query)
                    .Build()
                    .ToList()
                    .Select(minion => minion.Name.Split(' ')[0])
                    .Order()
                    .ToArray();
            }

            Assert.Equal(["Alice"], Names("BirthYear == 2000"));
            Assert.Equal(["Alice", "Charlie", "David"], Names("BirthMonth == 1"));

            // the unguarded form over three null rows, which used to throw in memory
            Assert.Equal(["Bob"], Names("FireYear == 2024"));
            Assert.Equal(["Alice"], Names("ReviewYear == 2024"));
        }
        finally
        {
            TestDatabase.Drop(context);
        }
    }
}
