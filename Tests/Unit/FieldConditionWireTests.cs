using System.Text.Json;
using Tests.Common;
using Weequery;
using Weequery.Interfaces;

namespace Tests.Unit;

/// <summary>
/// What a comparison naming a bound property looks like on the wire.
/// <para>
/// Every operand travels as text carrying what it is, see <see cref="ConditionValue{T}"/>, so a key and a value
/// that spell the same thing are two different payloads. That is the property the whole feature rests on: there
/// is nowhere for a key to arrive as bare text and be compared against as though it were one, which for a string
/// field would be a different question answered without complaint.
/// </para>
/// </summary>
public class FieldConditionWireTests
{
    private static PackedCondition Packed(string query)
    {
        return ConditionFunctions.ParseQuery(query)!.Pack();
    }

    private static string[] Matching(ICondition condition)
    {
        return [.. MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .BindProperty(nameof(Minion.Alias), "Ghost")
            .ApplyCondition(condition)
            .Build()
            .Select(minion => minion.Name)
            .ToList()
            .Order()];
    }

    // ---------- the shape ----------

    /// <summary>
    /// One list, in the order the operands were written, each saying which of the two it is
    /// </summary>
    [Fact]
    public void EveryOperandTravelsWithWhatItIs()
    {
        var packed = Packed("Pay IsIn (8000, [Morale], 12000)");

        Assert.Equal([ConditionValue.Raw("8000"), ConditionValue.Binding("Morale"), ConditionValue.Raw("12000")], packed.Values);
    }

    [Fact]
    public void AKeyIsNowhereAValueWouldBeRead()
    {
        var json = JsonSerializer.Serialize(Packed("Name IsIn ('Alice Fox', [Alias])"));

        // A value is written as the value alone, and a key as the object that says it is one. No value can be
        // written as an object, whatever it spells, so the two can never be confused.
        Assert.Contains("\"Values\":[\"Alice Fox\",{\"Source\":1,\"Value\":\"Alias\"}]", json);
    }

    /// <summary>
    /// A condition naming no property is the same shape, with every operand saying it is a value
    /// </summary>
    [Fact]
    public void AnOrdinaryConditionIsTheSameShape()
    {
        var packed = Packed("Pay IsIn (8000, 12000)");

        Assert.Equal([ConditionValue.Raw("8000"), ConditionValue.Raw("12000")], packed.Values);
    }

    /// <summary>
    /// An operator that asks about the field itself has nothing to carry
    /// </summary>
    [Fact]
    public void AnOperatorThatTakesNoValueCarriesNone()
    {
        Assert.Empty(Packed("Alias IsNull").Values);
    }

    // ---------- it still means what it meant ----------

    [Theory]
    [InlineData("Pay > [Morale]")]
    [InlineData("Pay IsIn (8000, [Morale])")]
    [InlineData("Pay IsBetween ([Morale], 12000)")]
    [InlineData("Name IsIn ('Alice Fox', [Alias])")]
    public void ItStillReadsBackAsItself(string query)
    {
        var condition = ConditionFunctions.ParseQuery(query)!;

        var json = JsonSerializer.Serialize(condition.Pack());
        var unpacked = JsonSerializer.Deserialize<PackedCondition>(json)!.Unpack();

        Assert.Contains(Assert.IsAssignableFrom<IBoundCondition>(unpacked).StringifyOperands(), operand => operand.NamesProperty);
        Assert.Equal(ConditionFunctions.ToQuery(condition), ConditionFunctions.ToQuery(unpacked));
    }

    /// <summary>
    /// A list whose only operand is a property, which is the shape with nothing to fall back on: every alias
    /// equals itself, so IsIn matches every row that has one and IsNotIn matches none
    /// </summary>
    [Fact]
    public void AListWhoseOnlyOperandIsAPropertySurvivesTheWire()
    {
        var isIn = new MultipleValueCondition<string>(Operator.IsIn, nameof(Minion.Alias), [ConditionValue.Binding("Ghost")]);
        var isNotIn = new MultipleValueCondition<string>(Operator.IsNotIn, nameof(Minion.Alias), [ConditionValue.Binding("Ghost")]);

        Assert.Equal(["Alice Fox", "Charlie Smith", "David Edgars"], Matching(isIn.Pack().Unpack()));
        Assert.Empty(Matching(isNotIn.Pack().Unpack()));
    }

    // ---------- the same text is not the same question ----------

    /// <summary>
    /// Why the source has to travel with the operand: the same text asks one question as a value and another as a
    /// key. 'Ghost' here is a second key for Alias, and Alice is the minion aliased Ghost, so read as text it
    /// matches her alone, while read as a key it asks about every row whose alias equals its alias.
    /// </summary>
    [Fact]
    public void AKeyAndAValueSpellingTheSameThingAreDifferentPayloads()
    {
        var asText = new PackedCondition(Operator.IsIn, nameof(Minion.Alias), [ConditionValue.Raw("Ghost")], []);
        var asKey = new PackedCondition(Operator.IsIn, nameof(Minion.Alias), [ConditionValue.Binding("Ghost")], []);

        Assert.Equal(["Alice Fox"], Matching(asText.Unpack()));
        Assert.Equal(["Alice Fox", "Charlie Smith", "David Edgars"], Matching(asKey.Unpack()));
    }

    /// <summary>
    /// A value written as bare text is a value, which is the shape every operand travelled in before one could
    /// name a property at all. So a payload from a sender that predates the feature reads as the condition it
    /// always was, rather than being refused or read as naming something.
    /// </summary>
    [Fact]
    public void APayloadOfBareTextReadsAsValues()
    {
        var packed = JsonSerializer.Deserialize<PackedCondition>("""{"Operator":10,"Field":"Alias","Values":["Ghost"]}""")!;

        Assert.Equal([ConditionValue.Raw("Ghost")], packed.Values);

        // 'Ghost' is a second key for Alias here, and read as a value it matches only the minion aliased Ghost
        Assert.Equal(["Alice Fox"], Matching(packed.Unpack()));
    }

    /// <summary>
    /// An operand written as an object with no value has nothing to compare against, so it is refused where it is
    /// read rather than becoming a condition holding nothing
    /// </summary>
    [Fact]
    public void AnOperandWithNoValueIsRefused()
    {
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<PackedCondition>("""{"Operator":10,"Field":"Alias","Values":[{"Source":1}]}"""));
    }
}
