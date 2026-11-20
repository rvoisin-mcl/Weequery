using Microsoft.EntityFrameworkCore;
using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// The ordering operators on a string binding. A string has no &lt; or &gt; of its own, so building one the way
/// the value types are built threw "The binary operator GreaterThan is not defined for the types 'System.String'"
/// out of the framework, well away from the query that caused it. They go through string.Compare now, which is
/// what EF Core recognises as the SQL operator.
/// </summary>
public class StringOrderingTests
{
    private static string[] InMemory(string query)
    {
        return MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition(query)
            .Build()
            .ToList()
            .Select(minion => minion.Name.Split(' ')[0])
            .Order()
            .ToArray();
    }


    // Alice Fox, Bob Samuelson, Charlie Smith, David Edgars

    [Theory]
    [InlineData("Name > 'Bob'", new[] { "Bob", "Charlie", "David" })]        // "Bob Samuelson" sorts after "Bob"
    [InlineData("Name >= 'Bob Samuelson'", new[] { "Bob", "Charlie", "David" })]
    [InlineData("Name < 'Bob'", new[] { "Alice" })]
    [InlineData("Name <= 'Bob Samuelson'", new[] { "Alice", "Bob" })]
    [InlineData("Name IsBetween ('B', 'D')", new[] { "Bob", "Charlie" })]
    [InlineData("Name IsNotBetween ('B', 'D')", new[] { "Alice", "David" })]
    public void TheOrderingOperatorsSelectTheExpectedRows(string query, string[] expected)
    {
        Assert.Equal(expected.Order().ToArray(), InMemory(query));
    }

    /// <summary>
    /// The property that matters most: one condition, one answer, wherever it runs.
    /// <para>
    /// This one has failed twice without leaving anything to read, and passed on every attempt to reproduce it, so
    /// it reports through <see cref="QueryAgreement"/>: on a disagreement it says which rows each side chose, what
    /// the provider was asked, what the database was holding, and whether either side says the same thing twice.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("Name > 'Bob'")]
    [InlineData("Name >= 'Bob Samuelson'")]
    [InlineData("Name < 'Bob'")]
    [InlineData("Name <= 'Bob Samuelson'")]
    [InlineData("Name IsBetween ('B', 'D')")]
    [InlineData("Name IsNotBetween ('B', 'D')")]
    [InlineData("Alias > 'B'")]
    [InlineData("Alias IsBetween ('B', 'S')")]
    public void InMemoryAgreesWithTheDatabase(string query)
    {
        QueryAgreement.AssertSameRows(query);
    }

    /// <summary>
    /// A null holds to the same rule the rest of the operators follow: it satisfies nothing, the negative ones
    /// included. Bob is the only minion with no alias.
    /// </summary>
    [Theory]
    [InlineData("Alias > 'A'")]
    [InlineData("Alias >= 'A'")]
    [InlineData("Alias < 'Z'")]
    [InlineData("Alias <= 'Z'")]
    [InlineData("Alias IsBetween ('A', 'Z')")]
    [InlineData("Alias IsNotBetween ('A', 'Z')")]
    public void ANullIsNotOrderedAgainstAnything(string query)
    {
        Assert.DoesNotContain("Bob", InMemory(query));
    }

    /// <summary>
    /// Which is the same partition the other operators make: matched, not matched, and the ones with no value
    /// </summary>
    [Fact]
    public void MatchedNotMatchedAndNullAccountForEveryRow()
    {
        var matched = InMemory("Alias > 'C'");
        var notMatched = InMemory("Alias <= 'C'");
        var nulls = InMemory("Alias IsNull");

        Assert.Empty(matched.Intersect(notMatched));
        Assert.Equal(4, matched.Length + notMatched.Length + nulls.Length);
    }

    /// <summary>
    /// It becomes the provider's own comparison rather than a function call, so an index on the column is usable
    /// and the value still arrives as a parameter
    /// </summary>
    [Theory]
    [MemberData(nameof(TestDatabase.AllProviders), MemberType = typeof(TestDatabase))]
    public void ItTranslatesToTheProvidersComparison(TestProvider provider)
    {
        using var context = TestDatabase.Create(provider);

        var queryString = context.Minions
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition("Name > 'Bob'")
            .Build()
            .ToQueryString();

        var statement = TestDatabase.StatementOnly(queryString);

        // The provider's own operator against a parameter, whatever it quotes its identifiers with
        Assert.Contains("Name", statement);
        Assert.Contains("> @Value", statement);

        // Not a function call standing in for one, which is what an untranslated Compare would have left behind
        Assert.DoesNotContain("CASE", statement);
        Assert.DoesNotContain("Compare", statement);
    }

    /// <summary>
    /// A range is still two comparisons rather than anything exotic
    /// </summary>
    [Fact]
    public void ARangeTranslatesToTwoComparisons()
    {
        using var context = TestDatabase.Create(TestProvider.Sqlite);

        var statement = TestDatabase.StatementOnly(context.Minions
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition("Name IsBetween ('A', 'M')")
            .Build()
            .ToQueryString());

        Assert.Contains(">=", statement);
        Assert.Contains("<=", statement);
    }

    /// <summary>
    /// The operators a string already had are untouched
    /// </summary>
    [Theory]
    [InlineData("Name == 'Alice Fox'", 1)]
    [InlineData("Name != 'Alice Fox'", 3)]
    [InlineData("Name StartsWith 'A'", 1)]
    [InlineData("Name IsIn ('Alice Fox', 'Bob Samuelson')", 2)]
    [InlineData("Alias IsNull", 1)]
    public void TheOtherOperatorsAreUnaffected(string query, int expected)
    {
        Assert.Equal(expected, InMemory(query).Length);
    }
}
