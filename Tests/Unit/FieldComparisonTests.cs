using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// A comparison whose right hand side is another bound property: "Pay &gt; [Salary]" rather than "Pay &gt; 10000".
/// <para>
/// The brackets are the whole of how a property is told from a value, in the language and on the wire alike. Text
/// is never guessed at: a value that happens to spell a binding key stays a value, which is what makes the feature
/// safe to expose to a caller.
/// </para>
/// </summary>
public class FieldComparisonTests
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

    // ---------- the comparison itself ----------

    /// <summary>
    /// Only Bob has a FireDate, and it is after his HireDate
    /// </summary>
    [Fact]
    public void TwoDatesCompare()
    {
        Assert.Equal(["Bob"], InMemory("HireDate < [FireDate]"));
        Assert.Empty(InMemory("HireDate > [FireDate]"));
    }

    /// <summary>
    /// Two strings compare through the same Compare the ordering operators use, and the substring family works on
    /// a property as readily as on a value
    /// </summary>
    [Fact]
    public void TwoStringsCompare()
    {
        Assert.Equal(["David"], InMemory("Name > [Alias]"));
        Assert.Equal(["Alice", "Charlie"], InMemory("Name < [Alias]"));
        Assert.Empty(InMemory("Name Contains [Alias]"));
        Assert.Equal(["Alice", "Charlie", "David"], InMemory("Name DoesNotContain [Alias]"));
    }

    [Fact]
    public void TwoOfTheSameEnumCompare()
    {
        Assert.Equal(["Alice", "Bob", "Charlie", "David"], InMemory("Classification == [Classification]"));
        Assert.Empty(InMemory("Classification != [Classification]"));
    }

    /// <summary>
    /// The null rule, applied to both sides: a row where either property has no value matches nothing, the
    /// negative operators included. Three minions have no FireDate.
    /// </summary>
    [Fact]
    public void ARowWithEitherSideMissingMatchesNothing()
    {
        var matched = InMemory("HireDate < [FireDate]");
        var notMatched = InMemory("HireDate >= [FireDate]");
        var missing = InMemory("FireDate IsNull");

        Assert.Empty(matched.Intersect(notMatched));
        Assert.Equal(4, matched.Length + notMatched.Length + missing.Length);
    }

    /// <summary>
    /// One condition, one answer, wherever it runs
    /// </summary>
    [Theory]
    [InlineData("HireDate < [FireDate]")]
    [InlineData("Name > [Alias]")]
    [InlineData("Name < [Alias]")]
    [InlineData("Name DoesNotContain [Alias]")]
    [InlineData("Classification == [Classification]")]
    public void InMemoryAgreesWithTheDatabase(string query)
    {
        Assert.Equal(InDatabase(query), InMemory(query));
    }

    /// <summary>
    /// It becomes the provider's own comparison of two columns, with no parameter at all, and the guard folds into
    /// the same predicate so one statement and one plan whatever the rows hold
    /// </summary>
    [Theory]
    [MemberData(nameof(TestDatabase.AllProviders), MemberType = typeof(TestDatabase))]
    public void ItTranslatesToAColumnComparisonWithNoParameters(TestProvider provider)
    {
        using var context = TestDatabase.Create(provider);

        var queryString = context.Minions
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition("HireDate < [FireDate]")
            .Build()
            .ToQueryString();

        var statement = TestDatabase.StatementOnly(queryString);

        Assert.Contains(nameof(Minion.HireDate), statement);
        Assert.Contains(nameof(Minion.FireDate), statement);
        Assert.Contains("IS NOT NULL", statement);
        Assert.Equal(0, TestDatabase.ParameterCount(queryString));
    }

    // ---------- what is refused ----------

    [Fact]
    public void ComparingTwoDifferentTypesIsRefused()
    {
        Assert.Throws<WeequeryException>(() => InMemory("Pay > [Name]"));
    }

    [Fact]
    public void AnUnboundPropertyOnTheRightIsRefused()
    {
        Assert.Throws<WeequeryException>(() => InMemory("Pay > [NotAField]"));
    }

    /// <summary>
    /// A bool orders no better against a property than against a value
    /// </summary>
    [Fact]
    public void OrderingTwoBoolsIsRefused()
    {
        Assert.Throws<WeequeryException>(() => InMemory("IsActive > [IsActive]"));
    }

    /// <summary>
    /// The operators that ask about the field itself have nothing to compare against, so there is nowhere for a
    /// property to go
    /// </summary>
    [Theory]
    [InlineData(Operator.IsNull)]
    [InlineData(Operator.IsNotNull)]
    public void AnOperatorThatTakesNoValueCannotNameAProperty(Operator op)
    {
        Assert.Throws<WeequeryException>(() => new OneValueCondition<string>(op, nameof(Minion.Pay), ConditionValue.Binding(nameof(Minion.Pay))));
    }

    /// <summary>
    /// A comparison against a property is the same type as a comparison against text, and the operand says which
    /// it is, so there is one way to build either and nothing to keep in step
    /// </summary>
    [Fact]
    public void AConditionThatNamesNoPropertyIsTheSameType()
    {
        var value = new OneValueCondition<string>(Operator.Equals, nameof(Minion.Pay), ConditionValue.Raw("10000"));
        var property = new OneValueCondition<string>(Operator.Equals, nameof(Minion.Pay), ConditionValue.Binding(nameof(Minion.Pay)));

        Assert.False(value.Value.NamesProperty);
        Assert.True(property.Value.NamesProperty);
    }

    [Fact]
    public void AMissingValueIsRefused()
    {
        Assert.Throws<WeequeryException>(() => new OneValueCondition<string>(Operator.Equals, nameof(Minion.Pay), (ConditionValue<string>)null!));
        Assert.Throws<WeequeryException>(() => new OneValueCondition<string>(Operator.Equals, nameof(Minion.Pay), ConditionValue.Binding("")));
    }

    [Fact]
    public void TheValuesAreCopied()
    {
        List<ConditionValue<string>> values = [ConditionValue.Binding(nameof(Minion.Pay))];

        var condition = new MultipleValueCondition<string>(Operator.IsIn, nameof(Minion.Pay), values);

        values.Add(ConditionValue.Raw("1"));

        Assert.Single(condition.Values);
    }

    // ---------- the language ----------

    /// <summary>
    /// The safety property the whole design rests on: a quoted value that spells a binding key is still a value.
    /// No minion is named "Alias".
    /// </summary>
    [Fact]
    public void AValueThatSpellsABindingKeyStaysAValue()
    {
        Assert.Empty(InMemory("Name == 'Alias'"));
        Assert.Empty(InMemory("Name == Alias"));
    }

    /// <summary>
    /// A list is written in parentheses, so the brackets mean one thing wherever they appear
    /// </summary>
    [Fact]
    public void AListOfValuesIsWrittenInParentheses()
    {
        Assert.Equal(["Alice", "David"], InMemory("Pay IsIn (12000, 8000)"));
        Assert.Equal(["Alice", "David"], InMemory("Pay IsBetween (8000, 12000)"));
    }

    /// <summary>
    /// The brackets used to hold a list as well, so one written that way is refused rather than read as naming a
    /// property. What the refusal says is not pinned here, only that there is one.
    /// </summary>
    [Fact]
    public void AListInBracketsIsRefused()
    {
        foreach (var query in new[] { "Pay IsIn [12000, 8000]", "Pay IsIn ['a', 'b']", "Pay IsBetween [8000, 12000]" })
        {
            Assert.Throws<WeequeryException>(() => ConditionFunctions.ParseQuery(query));
        }
    }

    [Fact]
    public void ABracketedNonNameIsRefused()
    {
        Assert.Throws<WeequeryException>(() => ConditionFunctions.ParseQuery("Pay > ['quoted']"));
    }

    // ---------- round trip and the wire ----------

    [Theory]
    [InlineData(QueryStyle.CSharp)]
    [InlineData(QueryStyle.Sql)]
    public void ItWritesAsBracketsAndReadsBack(QueryStyle style)
    {
        var condition = ConditionFunctions.ParseQuery("HireDate < [FireDate]")!;

        var text = condition.ToQuery(style);

        Assert.Contains("[FireDate]", text);

        var again = ConditionFunctions.ParseQuery(text)!;

        Assert.True(Assert.IsType<OneValueCondition<string>>(again).Value.NamesProperty);
        Assert.Equal(InMemory("HireDate < [FireDate]"), InMemory(text));
    }

    [Fact]
    public void ItSurvivesTheWire()
    {
        var condition = ConditionFunctions.ParseQuery("HireDate < [FireDate]")!;

        var json = JsonSerializer.Serialize(condition.Pack());
        var again = JsonSerializer.Deserialize<PackedCondition>(json)!.Unpack();

        var field = Assert.IsType<OneValueCondition<string>>(again);
        Assert.Equal(ValueSource.Binding, field.Value.Source);
        Assert.Equal(nameof(Minion.FireDate), field.Value.Value);

        Assert.Equal(["Bob"], MinionTestData.Minions().WithWeequery().BindProperties(Minion.Bindings).ApplyCondition(again).Build().Select(minion => minion.Name.Split(' ')[0]).ToArray());
    }

    /// <summary>
    /// A condition that names no property is the same list as one that does, holding operands that all say they
    /// are values, and it serializes to exactly what it always did: naming no property costs it nothing on the
    /// wire, since a value is written as the value alone.
    /// </summary>
    [Fact]
    public void AnOrdinaryConditionsJsonIsUnchanged()
    {
        var packed = ConditionFunctions.ParseQuery("Pay > 10000")!.Pack();

        Assert.Equal([ConditionValue.Raw("10000")], packed.Values);
        Assert.Contains("\"Values\":[\"10000\"]", JsonSerializer.Serialize(packed));
        Assert.DoesNotContain(nameof(ValueSource), JsonSerializer.Serialize(packed));
    }

    /// <summary>
    /// And the transport DTO carries one like anything else
    /// </summary>
    [Fact]
    public void ATransportConditionCarriesIt()
    {
        var transport = new TransportCondition("HireDate < [FireDate]");

        var count = MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition(transport.Unpack())
            .Build()
            .Count();

        Assert.Equal(1, count);
    }
}
