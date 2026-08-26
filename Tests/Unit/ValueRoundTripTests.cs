using System.Globalization;
using System.Text.Json;
using Tests.Common;
using Weequery;
using Weequery.Interfaces;

namespace Tests.Unit;

/// <summary>
/// Conditions are packed to strings and shipped between processes, so packing and unpacking must not depend on
/// the culture of whichever machine is running, and must not lose precision on the way through.
/// </summary>
public class ValueRoundTripTests
{
    private static T InCulture<T>(string culture, Func<T> action)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static readonly string[] Cultures = ["en-US", "de-DE", "fr-FR", "ja-JP", "ar-SA"];

    // ---------- packing is culture independent ----------

    [Fact]
    public void DecimalPacksTheSameInEveryCulture()
    {
        // Under de-DE this used to pack as "1234,56"
        var condition = new OneValueCondition<decimal>(Operator.Equals, nameof(Minion.Pay), 1234.56m);

        foreach (var culture in Cultures)
        {
            Assert.Equal("1234.56", InCulture(culture, () => condition.StringifyValues()[0]));
        }
    }

    [Fact]
    public void DoubleAndFloatPackTheSameInEveryCulture()
    {
        var doubleCondition = new OneValueCondition<double>(Operator.Equals, nameof(Minion.Pay), 0.1 + 0.2);
        var floatCondition = new OneValueCondition<float>(Operator.Equals, nameof(Minion.Pay), 1.5f);

        foreach (var culture in Cultures)
        {
            // Shortest round-trippable form, so no precision is quietly dropped
            Assert.Equal("0.30000000000000004", InCulture(culture, () => doubleCondition.StringifyValues()[0]));
            Assert.Equal("1.5", InCulture(culture, () => floatCondition.StringifyValues()[0]));
        }
    }

    [Fact]
    public void DateTimePacksTheSameInEveryCulture()
    {
        // Under en-US this used to pack as "12/25/2024 1:45:30 PM", under de-DE as "25.12.2024 13:45:30"
        var condition = new OneValueCondition<DateTime>(Operator.Equals, nameof(Minion.HireDate), new DateTime(2024, 12, 25, 13, 45, 30, 123));

        foreach (var culture in Cultures)
        {
            Assert.Equal("2024-12-25T13:45:30.1230000", InCulture(culture, () => condition.StringifyValues()[0]));
        }
    }

    [Fact]
    public void OtherValueTypesPackTheSameInEveryCulture()
    {
        var expectations = new (ICondition Condition, string Packed)[]
        {
            (new OneValueCondition<bool>(Operator.Equals, "F", true), "True"),
            (new OneValueCondition<int>(Operator.Equals, "F", -42), "-42"),
            (new OneValueCondition<long>(Operator.Equals, "F", long.MinValue), "-9223372036854775808"),
            (new OneValueCondition<Guid>(Operator.Equals, "F", new Guid("0f8fad5b-d9cb-469f-a165-70867728950e")), "0f8fad5b-d9cb-469f-a165-70867728950e"),
            (new OneValueCondition<TimeSpan>(Operator.Equals, "F", new TimeSpan(1, 2, 3, 4, 5)), "1.02:03:04.0050000"),
            (new OneValueCondition<DateTimeOffset>(Operator.Equals, "F", new DateTimeOffset(2024, 12, 25, 13, 45, 30, 123, TimeSpan.FromHours(-5))), "2024-12-25T13:45:30.1230000-05:00"),
            (new OneValueCondition<Classification>(Operator.Equals, "F", Classification.Irreplacable), "Irreplacable"),
        };

        foreach (var (condition, packed) in expectations)
        {
            foreach (var culture in Cultures)
            {
                Assert.Equal(packed, InCulture(culture, () => ((IBoundCondition)condition).StringifyValues()[0]));
            }
        }
    }

    // ---------- a condition packed under one culture still means the same thing under another ----------

    private static int CountMatching(ICondition condition)
    {
        return MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition(condition)
            .Build()
            .Count();
    }

    [Fact]
    public void DecimalFilterSurvivesACultureChange()
    {
        // Alice is paid 12000
        var condition = new OneValueCondition<decimal>(Operator.Equals, nameof(Minion.Pay), 12000.00m);

        foreach (var packIn in Cultures)
        {
            var packed = InCulture(packIn, () => (PackedCondition)condition.Pack());

            foreach (var readIn in Cultures)
            {
                Assert.Equal(1, InCulture(readIn, () => CountMatching(packed.Unpack())));
            }
        }
    }

    [Fact]
    public void DateFilterSurvivesACultureChange()
    {
        // David was hired 2025-08-15, the others in 2018
        var condition = new OneValueCondition<DateTime>(Operator.GreaterThan, nameof(Minion.HireDate), new DateTime(2020, 1, 1));

        foreach (var packIn in Cultures)
        {
            var packed = InCulture(packIn, () => (PackedCondition)condition.Pack());

            foreach (var readIn in Cultures)
            {
                Assert.Equal(1, InCulture(readIn, () => CountMatching(packed.Unpack())));
            }
        }
    }

    [Fact]
    public void FullJsonTransportSurvivesACultureChange()
    {
        // The scenario TransportCondition exists for: pack and serialize on one host, deserialize and run on another
        var condition = new TwoValueCondition<decimal>(Operator.IsBetween, nameof(Minion.Pay), 7999.50m, 12000.75m);

        var json = InCulture("de-DE", () => JsonSerializer.Serialize(new TransportCondition(condition)));

        // Alice 12000 and David 8000 are inside the range, Bob 0 and Charlie 19000 are not
        var matched = InCulture("en-US", () =>
        {
            var transport = JsonSerializer.Deserialize<TransportCondition>(json);
            return CountMatching(transport!.Unpack()!);
        });

        Assert.Equal(2, matched);
    }

    [Fact]
    public void QueryStringFilterSurvivesACultureChange()
    {
        foreach (var culture in Cultures)
        {
            Assert.Equal(1, InCulture(culture, () => CountMatching(ConditionFunctions.ParseQuery("(Pay == 12000.00)")!)));
            Assert.Equal(1, InCulture(culture, () => CountMatching(ConditionFunctions.ParseQuery("(HireDate > 2020-01-01)")!)));
        }
    }

    [Fact]
    public void ACommaDecimalIsRejectedRatherThanSilentlyMisread()
    {
        // "1234,56" is what a de-DE machine used to emit. Read as invariant it is not a number, and saying so
        // is far better than the old behaviour of quietly parsing it as 123456.
        Assert.Throws<WeequeryException>(() => CountMatching(ConditionFunctions.ParseQuery("(Pay == '1234,56')")!));
    }

    // ---------- precision and Kind are preserved ----------

    [Fact]
    public void SubSecondPrecisionSurvivesTheRoundTrip()
    {
        var original = new DateTime(2024, 12, 25, 13, 45, 30, 123);

        var packed = new OneValueCondition<DateTime>(Operator.Equals, "F", original).StringifyValues()[0];
        var parsed = (DateTime)ValueFormat.Parse(typeof(DateTime), packed);

        Assert.Equal(original, parsed);
        Assert.Equal(original.Millisecond, parsed.Millisecond);
    }

    [Fact]
    public void TicksSurviveTheRoundTrip()
    {
        var original = new DateTime(637_800_000_123_456_789L, DateTimeKind.Unspecified);

        var packed = ValueFormat.ToInvariantString(original);

        Assert.Equal(original.Ticks, ((DateTime)ValueFormat.Parse(typeof(DateTime), packed)).Ticks);
    }

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public void DateTimeKindSurvivesTheRoundTrip(DateTimeKind kind)
    {
        var original = new DateTime(2024, 12, 25, 13, 45, 30, 123, kind);

        var parsed = (DateTime)ValueFormat.Parse(typeof(DateTime), ValueFormat.ToInvariantString(original));

        Assert.Equal(kind, parsed.Kind);
        Assert.Equal(original, parsed);
    }

    [Fact]
    public void DateTimeOffsetKeepsItsOffset()
    {
        var original = new DateTimeOffset(2024, 12, 25, 13, 45, 30, 123, TimeSpan.FromHours(-5));

        var parsed = (DateTimeOffset)ValueFormat.Parse(typeof(DateTimeOffset), ValueFormat.ToInvariantString(original));

        Assert.Equal(original.Offset, parsed.Offset);
        Assert.Equal(original, parsed);
    }

    [Fact]
    public void AFilterOnASubSecondTimestampMatches()
    {
        // Before the round-trip format was used, packing dropped the milliseconds and this matched nothing
        var stamp = new DateTime(2024, 12, 25, 13, 45, 30, 123);

        var minions = new List<Minion> { new() { MinionID = Guid.NewGuid(), Name = "Precise Pete", HireDate = stamp } }.AsQueryable();

        var packed = (PackedCondition)new OneValueCondition<DateTime>(Operator.Equals, nameof(Minion.HireDate), stamp).Pack();

        var matched = minions
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition(packed.Unpack())
            .Build()
            .Count();

        Assert.Equal(1, matched);
    }

    // ---------- printed conditions stay parseable ----------

    [Fact]
    public void ToStringUsesInvariantValues()
    {
        var condition = new OneValueCondition<DateTime>(Operator.Equals, nameof(Minion.HireDate), new DateTime(2024, 12, 25, 13, 45, 30, 123));

        foreach (var culture in Cultures)
        {
            Assert.Equal("([HireDate] == 2024-12-25T13:45:30.1230000)", InCulture(culture, () => condition.ToString()));
        }
    }

    [Fact]
    public void ToStringEscapesQuotesSoOutputStaysParseable()
    {
        var condition = new OneValueCondition<string>(Operator.Equals, nameof(Minion.Alias), @"it's a \ mess");

        var printed = condition.ToString();
        var reparsed = Assert.IsType<OneValueCondition<string>>(ConditionFunctions.ParseQuery(printed));

        Assert.Equal(@"it's a \ mess", reparsed.Value.Value);
    }

    [Fact]
    public void PrintedDateConditionCanBeParsedBackAndStillMatches()
    {
        var condition = new OneValueCondition<DateTime>(Operator.GreaterThan, nameof(Minion.HireDate), new DateTime(2020, 1, 1));

        Assert.Equal(CountMatching(condition), CountMatching(ConditionFunctions.ParseQuery(condition.ToString())!));
    }

    [Fact]
    public void UnparseableValueNamesTheValueAndType()
    {
        Assert.Throws<WeequeryException>(() => CountMatching(ConditionFunctions.ParseQuery("(HireDate == 'not-a-date')")!));
    }

    [Fact]
    public void UnknownEnumMemberIsReported()
    {
        Assert.Throws<WeequeryException>(() => CountMatching(ConditionFunctions.ParseQuery("(Classification == Nonexistent)")!));
    }

    // ---------- a typed condition of any supported type survives the trip ----------

    /// <summary>
    /// One condition per supported value type, each built in code over its own type, packed, and applied. Packing
    /// writes the value as text and unpacking hands it back as a string, so what this exercises is the whole path
    /// from a typed value to a parameter read against the bound property's type.
    /// <para>
    /// Note the four written against Pay, which is a decimal: an int, a float, a double and a decimal all reach it,
    /// because the value is read against the property's type rather than the one the condition was built over.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryValueTypeStillSelectsItsRowAfterAPackRoundTrip()
    {
        // Each of these matches exactly one minion in the shared set
        ICondition[] conditions =
        [
            new OneValueCondition<bool>(Operator.Equals, nameof(Minion.IsActive), false).Pack(),
            new OneValueCondition<int>(Operator.Equals, nameof(Minion.Pay), 12000).Pack(),
            new OneValueCondition<float>(Operator.Equals, nameof(Minion.Pay), 8000).Pack(),
            new OneValueCondition<double>(Operator.Equals, nameof(Minion.Pay), 0).Pack(),
            new OneValueCondition<decimal>(Operator.Equals, nameof(Minion.Pay), 19000).Pack(),
            new OneValueCondition<string>(Operator.Equals, nameof(Minion.Name), "Alice Fox").Pack(),
            new OneValueCondition<DateTime>(Operator.Equals, nameof(Minion.BirthDate), new DateTime(2000, 1, 5)).Pack(),
            new OneValueCondition<Guid>(Operator.Equals, nameof(Minion.MinionID), new Guid(1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1)).Pack(),
            new OneValueCondition<Classification>(Operator.Equals, nameof(Minion.Classification), Classification.Irreplacable).Pack(),
        ];

        foreach (var condition in conditions)
        {
            Assert.Equal(1, CountMatching(condition));
        }
    }
}
