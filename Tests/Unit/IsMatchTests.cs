using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// The one operator that does not work everywhere. It matches a string against a regular expression, and where
/// that runs decides both what it costs and whether it works at all, see <see cref="Operator.IsMatch"/>.
/// </summary>
public class IsMatchTests
{
    private static string[] Matching(string query)
    {
        return [.. MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition(query)
            .Build()
            .Select(minion => minion.Name.Split(' ')[0])
            .ToList()
            .Order()];
    }

    // Alice Fox, Bob Samuelson, Charlie Smith, David Edgars. Aliases: Ghost, none, Snake, Babyface

    // ---------- in memory ----------

    [Theory]
    [InlineData("Name IsMatch '^A'", new[] { "Alice" })]
    [InlineData("Name IsMatch 'o'", new[] { "Alice", "Bob" })]
    [InlineData("Name IsMatch '^(Alice|Charlie)'", new[] { "Alice", "Charlie" })]
    [InlineData("Name IsMatch 'son$'", new[] { "Bob" })]
    [InlineData("Name IsMatch '.'", new[] { "Alice", "Bob", "Charlie", "David" })]
    [InlineData("Name IsMatch 'zzz'", new string[0])]
    public void ItMatchesAgainstThePattern(string query, string[] expected)
    {
        Assert.Equal(expected, Matching(query));
    }

    /// <summary>
    /// The pattern is a value like any other, so the punctuation a regular expression is made of survives being
    /// quoted
    /// </summary>
    [Fact]
    public void APatternKeepsItsPunctuation()
    {
        Assert.Equal(["Bob", "Charlie", "David"], Matching("Name IsMatch '^[^A]'"));
        Assert.Equal(["Alice", "Charlie"], Matching("Name IsMatch '^(Alice|Charlie) '"));
        Assert.Equal(["Alice"], Matching("Name IsMatch '^A.+ Fox$'"));
    }

    /// <summary>
    /// A pattern is written the way it is written everywhere else. The query language escapes only what it has to
    /// a quote, and a backslash in front of one so a backslash it does not recognise stays in the value
    /// rather than being eaten, which is what a regular expression is mostly made of.
    /// </summary>
    [Fact]
    public void ABackslashInAPatternIsPartOfThePattern()
    {
        Assert.Equal(["Alice"], Matching(@"Name IsMatch '^A\w+ Fox$'"));
        Assert.Equal(["Bob"], Matching(@"Name IsMatch '\bSam'"));
        Assert.Equal(["Alice", "Bob", "Charlie", "David"], Matching(@"Name IsMatch '^\p{Lu}'"));
    }

    /// <summary>
    /// And doubling it still means one backslash, so what ToQuery writes reads back as the same pattern as the
    /// hand written form it came from
    /// </summary>
    [Fact]
    public void ADoubledBackslashIsStillOneBackslash()
    {
        Assert.Equal(["Alice"], Matching(@"Name IsMatch '^A\\w+ Fox$'"));

        var written = ConditionFunctions.ParseQuery(@"Name IsMatch '^A\w+ Fox$'")!.ToQuery();

        Assert.Equal(Matching(@"Name IsMatch '^A\w+ Fox$'"), Matching(written));
    }

    /// <summary>
    /// A null does not match, exactly as it does not for the substring operators. Bob has no alias.
    /// </summary>
    [Fact]
    public void ANullDoesNotMatch()
    {
        Assert.Equal(["Alice", "Charlie", "David"], Matching("Alias IsMatch '.'"));

        // And Not brings it back, since it negates the guard with the test, see the remarks on Operator
        Assert.Equal(["Bob"], Matching("!(Alias IsMatch '.')"));
    }

    /// <summary>
    /// It reads the row's own value as the pattern as readily as one from the query
    /// </summary>
    [Fact]
    public void ThePatternCanBeAnotherBoundProperty()
    {
        // Every alias matches itself, so every minion that has one
        Assert.Equal(["Alice", "Charlie", "David"], Matching("Alias IsMatch [Alias]"));

        // And none of them fails to
        Assert.Empty(Matching("Alias DoesNotMatch [Alias]"));
    }

    // ---------- the negative ----------

    [Theory]
    [InlineData("Name DoesNotMatch '^A'", new[] { "Bob", "Charlie", "David" })]
    [InlineData("Name DoesNotMatch 'o'", new[] { "Charlie", "David" })]
    [InlineData("Name DoesNotMatch '.'", new string[0])]
    [InlineData("Name DoesNotMatch 'zzz'", new[] { "Alice", "Bob", "Charlie", "David" })]
    public void TheNegativeMatchesWhatTheMatchDoesNot(string query, string[] expected)
    {
        Assert.Equal(expected, Matching(query));
    }

    /// <summary>
    /// The distinction that makes it a negative operator rather than a negation: a row with no value matches
    /// neither, so the two of them plus the null rows partition the table. Wrapping IsMatch in Not is a different
    /// question, and brings the null rows back. Bob is the one with no alias.
    /// </summary>
    [Fact]
    public void ANullMatchesNeitherOfThem()
    {
        Assert.Equal(["Alice", "David"], Matching("Alias DoesNotMatch '^Sn'"));
        Assert.Equal(["Charlie"], Matching("Alias IsMatch '^Sn'"));

        // Between them they account for every minion but Bob, whose alias is null
        Assert.Equal(["Alice", "Bob", "David"], Matching("!(Alias IsMatch '^Sn')"));
    }

    /// <summary>
    /// So the pair behaves the way every other negative operator behaves
    /// </summary>
    [Theory]
    [InlineData("Alias", "^Sn")]
    [InlineData("Name", "^A")]
    [InlineData("Name", "zzz")]
    public void ThePairPartitionsTheRowsThatHaveAValue(string field, string pattern)
    {
        var matched = Matching($"{field} IsMatch '{pattern}'");
        var notMatched = Matching($"{field} DoesNotMatch '{pattern}'");
        var hasValue = Matching($"{field} IsNotNull");

        Assert.Empty(matched.Intersect(notMatched));
        Assert.Equal(hasValue, matched.Concat(notMatched).Order().ToArray());
    }

    // ---------- the language ----------

    [Theory]
    [InlineData(Operator.IsMatch, "IsMatch")]
    [InlineData(Operator.DoesNotMatch, "DoesNotMatch")]
    public void ItReadsAndWritesAsItself(Operator expected, string spelling)
    {
        var condition = ConditionFunctions.ParseQuery($"Name {spelling} '^A'")!;

        Assert.Equal(expected, Assert.IsType<OneValueCondition<string>>(condition).Operator);
        Assert.Equal($"([Name] {spelling} '^A')", condition.ToQuery());
        Assert.Equal($"([Name] {spelling} '^A')", condition.ToQuery(QueryStyle.Sql));
    }

    [Theory]
    [InlineData("IsMatch")]
    [InlineData("DoesNotMatch")]
    public void ItSurvivesBeingPackedAndUnpacked(string spelling)
    {
        var condition = ConditionFunctions.ParseQuery($"Name {spelling} '^A'")!;

        Assert.Equal(condition.ToQuery(), condition.Pack().Unpack().ToQuery());
    }

    /// <summary>
    /// An operator travels as its number, so IsMatch and DoesNotMatch go on the end rather than beside the other
    /// string operators. Getting that wrong would renumber every operator after it and change what payloads
    /// already in flight mean.
    /// </summary>
    [Fact]
    public void ItIsNumberedAfterEveryOperatorThatCameBeforeIt()
    {
        Assert.Equal(Operator.DoesNotMatch, Enum.GetValues<Operator>().Max());
        Assert.Equal(20, (int)Operator.Not);
    }

    /// <summary>
    /// A key spelling an operator is refused, and the set is read off the enum, so adding one reserves it
    /// </summary>
    [Fact]
    public void TheKeywordIsReserved()
    {
        Assert.Throws<WeequeryException>(() =>
            MinionTestData.Minions().WithWeequery().BindProperty(minion => minion.Name, nameof(Operator.IsMatch)));
    }

    // ---------- strings only ----------

    [Theory]
    [InlineData("Pay IsMatch '^1'")]
    [InlineData("HireDate IsMatch '^2'")]
    [InlineData("IsActive IsMatch 'true'")]
    [InlineData("Classification IsMatch 'Exp'")]
    [InlineData("Pay DoesNotMatch '^1'")]
    [InlineData("HireDate DoesNotMatch '^2'")]
    [InlineData("Classification DoesNotMatch 'Exp'")]
    public void ItIsRefusedForAPropertyThatIsNotAString(string query)
    {
        Assert.Throws<WeequeryException>(() => Matching(query));
    }

    // ---------- what each provider makes of it ----------

    private static string SqlFor(TestProvider provider, string query)
    {
        using var context = TestDatabase.Create(provider);

        return context.Minions
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition(query)
            .Build()
            .ToQueryString();
    }

    /// <summary>
    /// SQLite reads it as REGEXP and PostgreSQL as '~'. Producing SQL at all is most of the assertion, since EF
    /// throws rather than quietly evaluating on the client.
    /// </summary>
    [Theory]
    [InlineData(TestProvider.Sqlite, "REGEXP", "IsMatch")]
    [InlineData(TestProvider.PostgreSql, "~", "IsMatch")]
    [InlineData(TestProvider.Sqlite, "REGEXP", "DoesNotMatch")]
    [InlineData(TestProvider.PostgreSql, "~", "DoesNotMatch")]
    public void ItTranslatesWhereTheProviderHasAnOperatorForIt(TestProvider provider, string expected, string spelling)
    {
        var sql = TestDatabase.StatementOnly(SqlFor(provider, $"Name {spelling} '^A'"));

        Assert.Contains(expected, sql);

        // And the pattern goes as a parameter, like every other value, so the statement is the same whatever it is
        Assert.DoesNotContain("'^A'", sql);
    }

    /// <summary>
    /// SQL Server has no mapping for it, so the query fails when it is built. Pinned rather than left to be
    /// discovered: it is the one operator that is not universal, and this is where that shows.
    /// </summary>
    [Theory]
    [InlineData("IsMatch")]
    [InlineData("DoesNotMatch")]
    public void ItDoesNotTranslateOnSqlServer(string spelling)
    {
        Assert.ThrowsAny<Exception>(() => SqlFor(TestProvider.SqlServer, $"Name {spelling} '^A'"));
    }

    /// <summary>
    /// And the same for a pattern read from the row, which is the shape FieldComparison builds
    /// </summary>
    [Theory]
    [InlineData(TestProvider.Sqlite, "REGEXP")]
    [InlineData(TestProvider.PostgreSql, "~")]
    public void APatternFromAnotherPropertyTranslatesToo(TestProvider provider, string expected)
    {
        Assert.Contains(expected, SqlFor(provider, "Name IsMatch [Alias]"));
    }

    // ---------- the timeout ----------

    /// <summary>
    /// A pattern is caller input, and one like this costs time exponential in the length of a value that does not
    /// match. Bounded rather than left to run, since every other operator is bounded by the size of the data and
    /// this one is not.
    /// </summary>
    [Fact]
    public void ARunawayPatternIsStoppedRatherThanRunInMemory()
    {
        var previous = Inquiry<Minion>.MatchTimeout;
        Inquiry<Minion>.MatchTimeout = TimeSpan.FromMilliseconds(50);

        try
        {
            var minions = new List<Minion> { new() { MinionID = Guid.NewGuid(), Name = new string('a', 40) + "!" } }.AsQueryable();

            Assert.Throws<RegexMatchTimeoutException>(() => minions
                .WithWeequery()
                .BindProperties(Minion.Bindings)
                .ApplyCondition(@"Name IsMatch '^(a+)+$'")
                .Build()
                .ToList());
        }
        finally
        {
            Inquiry<Minion>.MatchTimeout = previous;
        }
    }

    /// <summary>
    /// And the same for a compiled predicate, which is in memory by definition
    /// </summary>
    [Fact]
    public void ARunawayPatternIsStoppedInACompiledPredicate()
    {
        var previous = Inquiry<Minion>.MatchTimeout;
        Inquiry<Minion>.MatchTimeout = TimeSpan.FromMilliseconds(50);

        try
        {
            var predicate = Inquiry<Minion>.BuildDelegate(Minion.Bindings, ConditionFunctions.ParseQuery(@"Name IsMatch '^(a+)+$'")!);

            Assert.Throws<RegexMatchTimeoutException>(() => predicate(new Minion { Name = new string('a', 40) + "!" }));
        }
        finally
        {
            Inquiry<Minion>.MatchTimeout = previous;
        }
    }

    /// <summary>
    /// The bound is not a limit on ordinary patterns, which finish in no time at all
    /// </summary>
    [Fact]
    public void AnOrdinaryPatternIsUnaffectedByTheBound()
    {
        var previous = Inquiry<Minion>.MatchTimeout;
        Inquiry<Minion>.MatchTimeout = TimeSpan.FromMilliseconds(50);

        try
        {
            Assert.Equal(["Alice"], Matching("Name IsMatch '^A'"));
        }
        finally
        {
            Inquiry<Minion>.MatchTimeout = previous;
        }
    }

    /// <summary>
    /// Removing the bound is the caller's to do, and leaves the framework's own default in charge
    /// </summary>
    [Fact]
    public void TheBoundCanBeRemoved()
    {
        var previous = Inquiry<Minion>.MatchTimeout;
        Inquiry<Minion>.MatchTimeout = Regex.InfiniteMatchTimeout;

        try
        {
            Assert.Equal(["Alice"], Matching("Name IsMatch '^A'"));
        }
        finally
        {
            Inquiry<Minion>.MatchTimeout = previous;
        }
    }
}
