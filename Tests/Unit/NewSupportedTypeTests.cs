using Microsoft.EntityFrameworkCore;
using Tests.Common;
using Weequery;
using Weequery.Interfaces;

namespace Tests.Unit;

/// <summary>
/// sbyte, DateOnly and TimeOnly bindings.
/// <para>
/// These were absent from ExpressionBuilder.SupportedTypes, so a property of one of them could not be bound at
/// all: Binding refused it with "property type is unsupported". Nothing else needed changing, since ValueFormat
/// already knew how to write and read all three and ValueExpressionBuilder handles any struct.
/// </para>
/// <para>
/// The shared test set carries Morale (sbyte, spanning the negative range and both extremes), ReviewDate
/// (DateOnly?, with a null row) and ShiftStart (TimeOnly).
/// </para>
/// </summary>
public class NewSupportedTypeTests
{
    private static string[] Matching(string query)
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

    // ---------- sbyte: Alice 5, Bob -3, Charlie -128, David 127 ----------

    [Fact]
    public void AnSByteBindsAndCompares()
    {
        Assert.Equal(["Alice"], Matching("Morale == 5"));
        Assert.Equal(["Bob", "Charlie"], Matching("Morale < 0"));
        Assert.Equal(["Alice", "David"], Matching("Morale > 0"));
        Assert.Equal(["Alice", "Bob"], Matching("Morale IsBetween (-3, 5)"));
    }

    /// <summary>
    /// The point of sbyte over byte: negative values, including the ends of its range
    /// </summary>
    [Fact]
    public void AnSByteHandlesItsSignedExtremes()
    {
        Assert.Equal(["Charlie"], Matching("Morale == -128"));
        Assert.Equal(["David"], Matching("Morale == 127"));
        Assert.Equal(["Charlie"], Matching($"Morale == {sbyte.MinValue}"));
        Assert.Equal(["David"], Matching($"Morale == {sbyte.MaxValue}"));
    }

    [Fact]
    public void AnSByteSupportsTheInFamily()
    {
        Assert.Equal(["Alice", "David"], Matching("Morale IsIn (5, 127)"));
        Assert.Equal(["Bob", "Charlie"], Matching("Morale IsNotIn (5, 127)"));
    }

    [Fact]
    public void AValueOutsideSByteRangeIsReported()
    {
        Assert.Throws<WeequeryException>(() => Matching("Morale == 200"));
    }

    // ---------- DateOnly: Alice 2024-01-15, Bob null, Charlie 2023-06-30, David 2025-03-01 ----------

    [Fact]
    public void ADateOnlyBindsAndCompares()
    {
        Assert.Equal(["Alice"], Matching("ReviewDate == 2024-01-15"));
        Assert.Equal(["Charlie"], Matching("ReviewDate < 2024-01-01"));
        Assert.Equal(["David"], Matching("ReviewDate > 2024-12-31"));
        Assert.Equal(["Alice", "Charlie"], Matching("ReviewDate IsBetween (2023-01-01, 2024-12-31)"));
    }

    [Fact]
    public void ANullableDateOnlyHandlesItsNullRow()
    {
        Assert.Equal(["Bob"], Matching("ReviewDate IsNull"));
        Assert.Equal(["Alice", "Charlie", "David"], Matching("ReviewDate IsNotNull"));

        // the null row must not satisfy a value comparison
        Assert.DoesNotContain("Bob", Matching("ReviewDate < 2030-01-01"));
    }

    [Fact]
    public void ADateOnlySupportsTheInFamily()
    {
        Assert.Equal(["Alice", "David"], Matching("ReviewDate IsIn (2024-01-15, 2025-03-01)"));
        Assert.Equal(["Charlie"], Matching("ReviewDate IsNotIn (2024-01-15, 2025-03-01)")); // Bob's null is unknown, not "not in"
    }

    [Fact]
    public void AnInvalidDateOnlyIsReported()
    {
        Assert.Throws<WeequeryException>(() => Matching("ReviewDate == 2024-13-45"));
    }

    // ---------- TimeOnly: Alice 09:00, Bob 17:30, Charlie 00:00, David 23:59 ----------

    [Fact]
    public void ATimeOnlyBindsAndCompares()
    {
        Assert.Equal(["Alice"], Matching("ShiftStart == 09:00:00"));
        Assert.Equal(["Alice", "Charlie"], Matching("ShiftStart < 12:00:00"));
        Assert.Equal(["Bob", "David"], Matching("ShiftStart > 12:00:00"));
        Assert.Equal(["Alice", "Bob"], Matching("ShiftStart IsBetween (09:00:00, 17:30:00)"));
    }

    [Fact]
    public void ATimeOnlyHandlesMidnightAndTheEndOfTheDay()
    {
        Assert.Equal(["Charlie"], Matching("ShiftStart == 00:00:00"));
        Assert.Equal(["David"], Matching("ShiftStart == 23:59:00"));
    }

    [Fact]
    public void ATimeOnlySupportsTheInFamily()
    {
        Assert.Equal(["Alice", "David"], Matching("ShiftStart IsIn (09:00:00, 23:59:00)"));
        Assert.Equal(["Bob", "Charlie"], Matching("ShiftStart IsNotIn (09:00:00, 23:59:00)"));
    }

    [Fact]
    public void AnInvalidTimeOnlyIsReported()
    {
        Assert.Throws<WeequeryException>(() => Matching("ShiftStart == 99:99:99"));
    }

    // ---------- the nullable branch of sbyte and TimeOnly, which the shared model does not carry ----------

    private sealed class Shift
    {
        public static readonly BindingRequest[] Bindings = [new("Tiny", null), new("Clock", null)];

        public sbyte? Tiny { get; set; }
        public TimeOnly? Clock { get; set; }
    }

    private static int MatchingShifts(string query)
    {
        return new List<Shift>
        {
            new() { Tiny = -5, Clock = new TimeOnly(9, 30) },
            new() { Tiny = null, Clock = null },
        }
        .AsQueryable()
        .WithWeequery()
        .BindProperties(Shift.Bindings)
        .ApplyCondition(query)
        .Build()
        .Count();
    }

    [Fact]
    public void ANullableSByteAndTimeOnlyBehaveLikeEveryOtherNullable()
    {
        Assert.Equal(1, MatchingShifts("Tiny == -5"));
        Assert.Equal(1, MatchingShifts("Tiny IsNull"));
        Assert.Equal(1, MatchingShifts("Tiny IsNotNull"));
        Assert.Equal(1, MatchingShifts("Tiny < 0"));

        Assert.Equal(1, MatchingShifts("Clock == 09:30:00"));
        Assert.Equal(1, MatchingShifts("Clock IsNull"));
        Assert.Equal(1, MatchingShifts("Clock IsNotNull"));
        Assert.Equal(1, MatchingShifts("Clock > 09:00:00"));
    }

    // ---------- round trip and invariant formatting ----------

    [Fact]
    public void TheNewTypesRoundTripThroughTheQueryLanguage()
    {
        foreach (var query in new[]
        {
            "Morale == -128",
            "Morale IsBetween (-3, 5)",
            "ReviewDate == 2024-01-15",
            "ReviewDate IsNull",
            "ShiftStart == 09:00:00",
            "ShiftStart IsIn (09:00:00, 23:59:00)",
        })
        {
            var condition = ConditionFunctions.ParseQuery(query)!;

            Assert.Equal(Matching(query), Matching(ConditionFunctions.ParseQuery(condition.ToQuery())!.ToQuery()));
        }
    }

    [Fact]
    public void TheNewTypesPackToTheirRoundTripFormat()
    {
        Assert.Equal(["-128"], new OneValueCondition<sbyte>(Operator.Equals, "Morale", -128).StringifyValues());
        Assert.Equal(["2024-01-15"], new OneValueCondition<DateOnly>(Operator.Equals, "ReviewDate", new DateOnly(2024, 1, 15)).StringifyValues());
        Assert.Equal(["09:30:00.0000000"], new OneValueCondition<TimeOnly>(Operator.Equals, "ShiftStart", new TimeOnly(9, 30)).StringifyValues());
    }

    [Fact]
    public void ATypedConditionOfEachNewTypeWorks()
    {
        Assert.Equal(["Charlie"], Matching(new OneValueCondition<sbyte>(Operator.Equals, nameof(Minion.Morale), -128)));
        Assert.Equal(["Alice"], Matching(new OneValueCondition<DateOnly>(Operator.Equals, nameof(Minion.ReviewDate), new DateOnly(2024, 1, 15))));
        Assert.Equal(["Alice"], Matching(new OneValueCondition<TimeOnly>(Operator.Equals, nameof(Minion.ShiftStart), new TimeOnly(9, 0))));
    }

    private static string[] Matching(ICondition condition)
    {
        return MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition(condition)
            .Build()
            .ToList()
            .Select(minion => minion.Name.Split(' ')[0])
            .Order()
            .ToArray();
    }

    // ---------- and they translate on every provider ----------

    [Theory]
    [MemberData(nameof(TestDatabase.AllProviders), MemberType = typeof(TestDatabase))]
    public void TheNewTypesTranslate(TestProvider provider)
    {
        using var context = TestDatabase.Create(provider);

        foreach (var query in new[]
        {
            "Morale == -5",
            "Morale > 0",
            "Morale IsIn (1, 2)",
            "ReviewDate == 2024-01-15",
            "ReviewDate > 2024-01-01",
            "ReviewDate IsNull",
            "ShiftStart == 09:00:00",
            "ShiftStart > 12:00:00",
            "ShiftStart IsIn (09:00:00)",
        })
        {
            var sql = context.Minions
                .WithWeequery()
                .BindProperties(Minion.Bindings)
                .ApplyCondition(query)
                .Build()
                .ToQueryString();

            Assert.Contains("SELECT", sql);
        }
    }

    [Fact]
    public void RowsComeBackCorrectlyFromARealDatabase()
    {
        var context = TestDatabase.CreateSeeded(TestProvider.Sqlite);
        try
        {
            string[] Names(string query)
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

            Assert.Equal(["Bob", "Charlie"], Names("Morale < 0"));
            Assert.Equal(["Charlie"], Names("Morale == -128"));
            Assert.Equal(["Alice"], Names("ReviewDate == 2024-01-15"));
            Assert.Equal(["Bob"], Names("ReviewDate IsNull"));
            Assert.Equal(["Alice", "Charlie"], Names("ShiftStart < 12:00:00"));
            Assert.Equal(["David"], Names("ShiftStart == 23:59:00"));
        }
        finally
        {
            TestDatabase.Drop(context);
        }
    }
}
