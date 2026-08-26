using System.Text.Json;
using System.Text.Json.Serialization;
using Tests.Common;
using Weequery;
using Weequery.Interfaces;

namespace Tests.Unit;

/// <summary>
/// PackedCondition exists to be serialized, so its JSON contract is worth pinning directly rather than only being
/// covered incidentally by the tests that ship a TransportCondition.
/// <para>
/// The specific trap: the type has several parameterized constructors, and System.Text.Json refuses to guess
/// between them. Without a JsonConstructor it throws NotSupportedException the moment anything tries to
/// deserialize one, which the compiler cannot warn about and which only shows up at runtime.
/// </para>
/// </summary>
public class PackedConditionSerializationTests
{
    private static PackedCondition RoundTripThroughJson(PackedCondition packed)
    {
        var json = JsonSerializer.Serialize(packed);

        var back = JsonSerializer.Deserialize<PackedCondition>(json);
        Assert.NotNull(back);

        return back;
    }

    [Fact]
    public void AValueConditionSurvivesJson()
    {
        var packed = (PackedCondition)new OneValueCondition<decimal>(Operator.Equals, nameof(Minion.Pay), 12000m).Pack();

        var back = RoundTripThroughJson(packed);

        Assert.Equal(Operator.Equals, back.Operator);
        Assert.Equal(nameof(Minion.Pay), back.Field);
        Assert.Equal([ConditionValue.Raw("12000")], back.Values);
    }

    [Fact]
    public void AMultiValueConditionSurvivesJson()
    {
        var packed = (PackedCondition)new TwoValueCondition<decimal>(Operator.IsBetween, nameof(Minion.Pay), 8000m, 12000m).Pack();

        Assert.Equal([ConditionValue.Raw("8000"), ConditionValue.Raw("12000")], RoundTripThroughJson(packed).Values);
    }

    [Fact]
    public void AValuelessConditionSurvivesJson()
    {
        var packed = (PackedCondition)new NoValueCondition(Operator.IsNull, nameof(Minion.Alias)).Pack();

        var back = RoundTripThroughJson(packed);

        Assert.Equal(Operator.IsNull, back.Operator);
        Assert.Empty(back.Values);
    }

    [Fact]
    public void AConjunctionSurvivesJson()
    {
        var condition = new ConjunctionCondition(Operator.And,
        [
            new OneValueCondition<decimal>(Operator.GreaterThan, nameof(Minion.Pay), 10000m),
            new OneValueCondition<bool>(Operator.Equals, nameof(Minion.IsActive), true),
        ]);

        var back = RoundTripThroughJson((PackedCondition)condition.Pack());

        Assert.Equal(Operator.And, back.Operator);
        Assert.Equal(2, back.Conditions.Count);
    }

    [Fact]
    public void ANegationSurvivesJson()
    {
        var condition = new NotCondition(Operator.Not, new OneValueCondition<decimal>(Operator.GreaterThan, nameof(Minion.Pay), 10000m));

        var back = RoundTripThroughJson((PackedCondition)condition.Pack());

        Assert.Equal(Operator.Not, back.Operator);
        Assert.Single(back.Conditions);
    }

    [Fact]
    public void ANestedTreeSurvivesJsonAndSelectsTheSameRows()
    {
        var condition = new ConjunctionCondition(Operator.And,
        [
            new NotCondition(Operator.Not, new OneValueCondition<decimal>(Operator.GreaterThan, nameof(Minion.Pay), 10000m)),
            new ConjunctionCondition(Operator.Or,
            [
                new OneValueCondition<bool>(Operator.Equals, nameof(Minion.IsActive), true),
                new NoValueCondition(Operator.IsNull, nameof(Minion.Alias)),
            ]),
        ]);

        string[] Matching(ICondition applied)
        {
            return MinionTestData.Minions()
                .WithWeequery()
                .BindProperties(Minion.Bindings)
                .ApplyCondition(applied)
                .Build()
                .ToList()
                .Select(minion => minion.Name)
                .Order()
                .ToArray();
        }

        var back = RoundTripThroughJson((PackedCondition)condition.Pack());

        Assert.Equal(Matching(condition), Matching(back.Unpack()));
    }

    [Fact]
    public void ATransportConditionSurvivesJson()
    {
        // The wrapper the library actually ships, which holds a PackedCondition
        var transport = new TransportCondition(new OneValueCondition<decimal>(Operator.Equals, nameof(Minion.Pay), 12000m));

        var back = JsonSerializer.Deserialize<TransportCondition>(JsonSerializer.Serialize(transport));

        Assert.NotNull(back);
        Assert.Equal("([Pay] == '12000')", back.Unpack()!.ToQuery());
    }

    // ---------- the non-null defaults hold for a payload that leaves members out ----------

    /// <summary>
    /// Why the parameterless constructor is the one marked JsonConstructor. It runs the property initializers, so a
    /// payload missing Values or Conditions still deserializes to empty lists. Had the four argument constructor
    /// been marked instead, the missing members would arrive as default, meaning null on properties that are
    /// declared non-nullable.
    /// </summary>
    [Fact]
    public void MembersMissingFromThePayloadKeepTheirNonNullDefaults()
    {
        var back = JsonSerializer.Deserialize<PackedCondition>("""{"Operator":2,"Field":"Pay"}""");

        Assert.NotNull(back);
        Assert.Equal(Operator.Equals, back.Operator);
        Assert.Equal("Pay", back.Field);
        Assert.NotNull(back.Values);
        Assert.Empty(back.Values);
        Assert.NotNull(back.Conditions);
        Assert.Empty(back.Conditions);
    }

    [Fact]
    public void AnEmptyPayloadDeserializesToTheDefaults()
    {
        var back = JsonSerializer.Deserialize<PackedCondition>("{}");

        Assert.NotNull(back);
        Assert.Equal(string.Empty, back.Field);
        Assert.Empty(back.Values);
        Assert.Empty(back.Conditions);
    }

    // ---------- the compact form the operands travel in ----------

    /// <summary>
    /// An operand that is a value is written as the value alone, and only one naming a bound property carries a
    /// source. Both come back as what they were.
    /// </summary>
    [Fact]
    public void OperandsRoundTripInTheirCompactForm()
    {
        var packed = new PackedCondition(Operator.IsIn, "Pay", [ConditionValue.Raw("8000"), ConditionValue.Binding("Ceiling")], []);

        var json = JsonSerializer.Serialize(packed);

        Assert.Equal("""{"Operator":10,"Field":"Pay","Values":["8000",{"Source":1,"Value":"Ceiling"}],"Conditions":[]}""", json);
        Assert.Equal(packed.Values, RoundTripThroughJson(packed).Values);
    }

    /// <summary>
    /// The converter writes the two members itself, so it has to honour a naming policy rather than assume the
    /// names it was given
    /// </summary>
    [Fact]
    public void AnOperandNamingAPropertyHonoursTheNamingPolicy()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var packed = new PackedCondition(Operator.Equals, "Pay", [ConditionValue.Binding("Ceiling")], []);

        var json = JsonSerializer.Serialize(packed, options);

        Assert.Contains("\"source\":1,\"value\":\"Ceiling\"", json);
        Assert.Equal(packed.Values, JsonSerializer.Deserialize<PackedCondition>(json, options)!.Values);
    }

    /// <summary>
    /// And it reads the source through whatever converter the caller registered for the enum, rather than
    /// assuming it arrives as a number
    /// </summary>
    [Fact]
    public void AnOperandNamingAPropertyHonoursAStringEnumConverter()
    {
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var packed = new PackedCondition(Operator.Equals, "Pay", [ConditionValue.Binding("Ceiling")], []);

        var json = JsonSerializer.Serialize(packed, options);

        Assert.Contains($"\"Source\":\"{nameof(ValueSource.Binding)}\"", json);
        Assert.Equal(packed.Values, JsonSerializer.Deserialize<PackedCondition>(json, options)!.Values);
    }

    /// <summary>
    /// A member a later version added is stepped over rather than refused, so an operand carrying one still reads
    /// </summary>
    [Fact]
    public void AnOperandCarryingSomethingUnknownStillReads()
    {
        var back = JsonSerializer.Deserialize<PackedCondition>("""{"Operator":2,"Field":"Pay","Values":[{"Source":1,"Value":"Ceiling","Added":{"By":"a later version"}}]}""");

        Assert.NotNull(back);
        Assert.Equal([ConditionValue.Binding("Ceiling")], back.Values);
    }
}
