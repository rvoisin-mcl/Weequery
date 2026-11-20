using Microsoft.EntityFrameworkCore;
using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// The ordering operators on an enum binding. C# compares an enum by converting to the type it is based on, and it
/// does that at compile time, so building the comparison the way a numeric one is built failed with "the binary
/// operator GreaterThan is not defined for the types 'Classification' and 'Classification'" reported, worse, as
/// a failure to parse a value that had parsed perfectly well.
/// <para>
/// Nothing in the suite ordered an enum before: every enum test used equality, IsIn or IsNull, while the other
/// supported types were ordered carefully. That is why it went unnoticed.
/// </para>
/// </summary>
public class EnumOrderingTests
{
    public enum Rank { Low = 1, Mid = 2, High = 3 }

    public enum Size : byte { Small = 1, Big = 2, Huge = 3 }

    public enum Signed : sbyte { Below = -1, Zero = 0, Above = 1 }

    private class Thing
    {
        public Rank Rank { get; set; }
        public Size Size { get; set; }
        public Signed Signed { get; set; }
        public Rank? Maybe { get; set; }
    }

    private static IQueryable<Thing> Things()
    {
        return new List<Thing>
        {
            new() { Rank = Rank.Low, Size = Size.Small, Signed = Signed.Below, Maybe = Rank.Low },
            new() { Rank = Rank.Mid, Size = Size.Big, Signed = Signed.Zero, Maybe = null },
            new() { Rank = Rank.High, Size = Size.Huge, Signed = Signed.Above, Maybe = Rank.High },
        }.AsQueryable();
    }

    private static int Count(string query)
    {
        return Things()
            .WithWeequery()
            .BindProperty(thing => thing.Rank)
            .BindProperty(thing => thing.Size)
            .BindProperty(thing => thing.Signed)
            .BindProperty(thing => thing.Maybe, "Maybe")
            .ApplyCondition(query)
            .Build()
            .Count();
    }

    [Theory]
    [InlineData("Rank > Low", 2)]
    [InlineData("Rank >= Mid", 2)]
    [InlineData("Rank < High", 2)]
    [InlineData("Rank <= Mid", 2)]
    [InlineData("Rank IsBetween (Low, Mid)", 2)]
    [InlineData("Rank IsNotBetween (Low, Mid)", 1)]
    public void TheOrderingOperatorsWorkOnAnEnum(string query, int expected)
    {
        Assert.Equal(expected, Count(query));
    }

    /// <summary>
    /// The conversion is to whatever the enum is based on, not to int, so the ones that are not int have to work
    /// too including a signed one with a negative member, where treating the bits as unsigned would order wrongly
    /// </summary>
    [Theory]
    [InlineData("Size > Small", 2)]
    [InlineData("Size IsBetween (Small, Big)", 2)]
    [InlineData("Signed > Below", 2)]
    [InlineData("Signed < Zero", 1)]
    [InlineData("Signed IsBetween (Below, Zero)", 2)]
    public void ItWorksWhateverTheEnumIsBasedOn(string query, int expected)
    {
        Assert.Equal(expected, Count(query));
    }

    /// <summary>
    /// And a nullable enum keeps the rule the rest of the operators follow: the row with no value is not ordered
    /// against anything, the negative operators included
    /// </summary>
    [Theory]
    [InlineData("Maybe > Low", 1)]
    [InlineData("Maybe < High", 1)]
    [InlineData("Maybe IsBetween (Low, High)", 2)]
    [InlineData("Maybe IsNotBetween (Low, High)", 0)]
    [InlineData("Maybe IsNull", 1)]
    public void ANullEnumIsNotOrderedAgainstAnything(string query, int expected)
    {
        Assert.Equal(expected, Count(query));
    }

    /// <summary>
    /// The operators an enum already had are untouched
    /// </summary>
    [Theory]
    [InlineData("Rank == Mid", 1)]
    [InlineData("Rank != Mid", 2)]
    [InlineData("Rank IsIn (Low, High)", 2)]
    public void TheOtherOperatorsAreUnaffected(string query, int expected)
    {
        Assert.Equal(expected, Count(query));
    }

    // ---------- against a provider ----------

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

    private static string[] InDatabase(string query)
    {
        var context = TestDatabase.CreateSeeded(TestProvider.Sqlite);
        try
        {
            return context.Minions
                .WithWeequery()
                .BindProperties(Minion.Bindings)
                .ApplyCondition(query)
                .Build()
                .ToList()
                .Select(minion => minion.Name.Split(' ')[0])
                .Order()
                .ToArray();
        }
        finally
        {
            TestDatabase.Drop(context);
        }
    }

    /// <summary>
    /// One condition, one answer, wherever it runs. David is the only Irreplacable.
    /// </summary>
    [Theory]
    [InlineData("Classification > Expendible")]
    [InlineData("Classification >= Irreplacable")]
    [InlineData("Classification < Irreplacable")]
    [InlineData("Classification IsBetween (Expendible, Irreplacable)")]
    [InlineData("Classification IsNotBetween (Expendible, Expendible)")]
    public void InMemoryAgreesWithTheDatabase(string query)
    {
        // Through the harness rather than a bare Equal, so a disagreement arrives diagnosable, see QueryAgreement
        QueryAgreement.AssertSameRows(query);
    }

    [Fact]
    public void TheDatabaseAnswersWithTheRightRows()
    {
        Assert.Equal(["David"], InDatabase("Classification > Expendible"));
    }

    /// <summary>
    /// The conversion is what the value is stored as, so the provider still sees a plain comparison against a
    /// parameter: no cast in the statement, and the value not written into it
    /// </summary>
    [Theory]
    [MemberData(nameof(TestDatabase.AllProviders), MemberType = typeof(TestDatabase))]
    public void ItTranslatesToAComparisonAgainstAParameter(TestProvider provider)
    {
        using var context = TestDatabase.Create(provider);

        var queryString = context.Minions
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition("Classification > Expendible")
            .Build()
            .ToQueryString();

        var statement = TestDatabase.StatementOnly(queryString);

        Assert.Contains(nameof(Minion.Classification), statement);
        Assert.Contains("> @Value", statement);
        Assert.DoesNotContain("CAST", statement.ToUpperInvariant());
        Assert.Equal(1, TestDatabase.ParameterCount(queryString));
    }

    [Fact]
    public void ARangeTranslatesToTwoComparisonsAndTwoParameters()
    {
        using var context = TestDatabase.Create(TestProvider.Sqlite);

        var queryString = context.Minions
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition("Classification IsBetween (Expendible, Irreplacable)")
            .Build()
            .ToQueryString();

        Assert.Contains(">=", TestDatabase.StatementOnly(queryString));
        Assert.Contains("<=", TestDatabase.StatementOnly(queryString));
        Assert.Equal(2, TestDatabase.ParameterCount(queryString));
    }

    // ---------- and when something really is wrong ----------

    /// <summary>
    /// A value that genuinely will not parse is still refused. It used to be refused for a value that had parsed
    /// too, because the catch covered the build as well as the parse.
    /// </summary>
    [Fact]
    public void AValueThatWillNotParseIsRefused()
    {
        Assert.Throws<WeequeryException>(() => InMemory("Classification > Nonsense"));
    }

    [Fact]
    public void TheSameHoldsForTheOtherTypes()
    {
        Assert.Throws<WeequeryException>(() => InMemory("Pay > 'not a number'"));
    }
}
