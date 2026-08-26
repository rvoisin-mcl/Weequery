using Tests.Common;
using Weequery;
using Weequery.Interfaces;

namespace Tests.Unit;

/// <summary>
/// ConditionFunctions.ToQuery is the inverse of ConditionFunctions.ParseQuery, so a condition can be written to a
/// string and read back.
/// <para>
/// The round trip preserves meaning, not object identity: values come back as strings, the same way
/// PackedCondition.Unpack produces them. So these tests check that the text re-parses, that writing the re-parsed
/// condition gives the same text again (the round trip is stable), and that both select the same rows.
/// </para>
/// </summary>
public class QueryRoundTripTests
{
    /// <summary>
    /// Every query shape the language supports
    /// </summary>
    public static TheoryData<string> Queries()
    {
        return new TheoryData<string>(
            "Pay == 12000",
            "Pay != 12000",
            "Pay > 12000",
            "Pay >= 12000",
            "Pay < 12000",
            "Pay <= 12000",
            "Pay IsBetween (8000, 12000)",
            "Pay IsNotBetween (8000, 12000)",
            "Pay IsIn (8000, 12000)",
            "Pay IsNotIn (8000, 12000)",
            "Pay IsIn (8000)",
            "Pay IsIn ()",
            "Pay IsNotIn ()",
            "Name StartsWith 'Al'",
            "Name DoesNotStartWith 'Al'",
            "Name EndsWith 'Fox'",
            "Name DoesNotEndWith 'Fox'",
            "Name Contains 'li'",
            "Name DoesNotContain 'li'",
            "Alias IsNull",
            "Alias IsNotNull",
            "Alias == null",
            "Alias != null",
            "IsActive == true",
            "Classification == Irreplacable",
            "MinionID == 0f8fad5b-d9cb-469f-a165-70867728950e",
            "HireDate > 2020-01-01",
            "BirthDate IsNull",
            "!(Pay > 10000)",
            "!!(Pay > 10000)",
            "(Pay > 10000) && (IsActive == true)",
            "(Pay > 10000) || (IsActive == true)",
            "(Pay > 1) && (Pay > 2) && (Pay > 3)",
            "!(Pay > 10000) && (IsActive == true)",
            "((Pay > 15000) || (Pay < 5000)) && (IsActive == true)",
            "(Pay > 15000) && (IsActive == false) || (Name == 'Alice Fox')",
            "(((IsActive == true) && ((Pay > 5000) && !(Pay > 10000))) || (Name == 'Charlie Smith'))",
            "Name == ''",
            @"Name == 'it\'s'",
            @"Name == 'back\\slash'",
            "Name == 'has space'",
            "Name == 'a,b'",
            "Name == '(paren)'",
            "Name == '&& ||'",
            "Name == 'null'",
            "Name == 'AND'",
            "Name == 'Alice Fox'");
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public void WritingAParsedConditionAndReparsingItIsStable(string query)
    {
        var first = ConditionFunctions.ParseQuery(query)!;
        var written = first.ToQuery();

        // What was written must parse
        var second = ConditionFunctions.ParseQuery(written);
        Assert.NotNull(second);

        // and writing it again must give the same text, so the round trip has settled rather than drifting
        Assert.Equal(written, second.ToQuery());
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public void ARoundTrippedConditionSelectsTheSameRows(string query)
    {
        string[] Matching(ICondition condition)
        {
            return MinionTestData.Minions()
                .WithWeequery()
                .BindProperties(Minion.Bindings)
                .ApplyCondition(condition)
                .Build()
                .ToList()
                .Select(minion => minion.Name)
                .Order()
                .ToArray();
        }

        var original = ConditionFunctions.ParseQuery(query)!;
        var roundTripped = ConditionFunctions.ParseQuery(original.ToQuery())!;

        Assert.Equal(Matching(original), Matching(roundTripped));
    }

    // ---------- hand built conditions, which the parser could never have produced ----------

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

    private static ICondition RoundTrip(ICondition condition)
    {
        return ConditionFunctions.ParseQuery(condition.ToQuery())!;
    }

    /// <summary>
    /// Assert the round trip for a condition: it writes to something the parser accepts, the result selects the
    /// same rows, and writing it again is stable.
    /// <para>
    /// Note where stability starts. A typed condition writes its values bare, because a decimal or a date reads
    /// better unquoted, while a re-parsed condition holds strings and writes them quoted. So the text settles from
    /// the parsed form onward rather than on the first hop, which is why this compares the second and third writes
    /// rather than the first and second.
    /// </para>
    /// </summary>
    private static void AssertRoundTrips(ICondition condition, Func<ICondition, string[]> select)
    {
        var written = condition.ToQuery();

        var parsed = ConditionFunctions.ParseQuery(written);
        Assert.NotNull(parsed);

        Assert.Equal(select(condition), select(parsed));
        Assert.Equal(parsed.ToQuery(), RoundTrip(parsed).ToQuery());
    }

    [Fact]
    public void TypedValueConditionsRoundTrip()
    {
        var conditions = new ICondition[]
        {
            new OneValueCondition<decimal>(Operator.Equals, nameof(Minion.Pay), 12000m),
            new OneValueCondition<bool>(Operator.Equals, nameof(Minion.IsActive), false),
            new OneValueCondition<Guid>(Operator.Equals, nameof(Minion.MinionID), new Guid(1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1)),
            new OneValueCondition<DateTime>(Operator.Equals, nameof(Minion.BirthDate), new DateTime(2000, 1, 5)),
            new OneValueCondition<Classification>(Operator.Equals, nameof(Minion.Classification), Classification.Irreplacable),
            new OneValueCondition<string>(Operator.Equals, nameof(Minion.Name), "Alice Fox"),
            new TwoValueCondition<decimal>(Operator.IsBetween, nameof(Minion.Pay), 8000m, 12000m),
            new MultipleValueCondition<string>(Operator.IsIn, nameof(Minion.Name), ["Alice Fox", "Bob Samuelson"]),
        };

        foreach (var condition in conditions)
        {
            AssertRoundTrips(condition, Matching);
        }
    }

    [Fact]
    public void ATimestampWithSubSecondPrecisionSurvivesTheRoundTrip()
    {
        var stamp = new DateTime(2024, 12, 25, 13, 45, 30, 123);
        var condition = new OneValueCondition<DateTime>(Operator.Equals, nameof(Minion.HireDate), stamp);

        // The full round-trip format, so no precision is dropped on the way through the text
        Assert.Equal("([HireDate] == 2024-12-25T13:45:30.1230000)", condition.ToQuery());

        // and once parsed the value is a string, so it comes back quoted, and stays that way
        Assert.Equal("([HireDate] == '2024-12-25T13:45:30.1230000')", RoundTrip(condition).ToQuery());

        AssertRoundTrips(condition, Matching);
    }

    [Fact]
    public void ANestedHandBuiltTreeRoundTrips()
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

        AssertRoundTrips(condition, Matching);
    }

    [Fact]
    public void APackedConditionCanBeWrittenAsAQuery()
    {
        // PackedCondition had no ToString at all, so it used to render as its type name
        var packed = (PackedCondition)new OneValueCondition<decimal>(Operator.Equals, nameof(Minion.Pay), 12000m).Pack();

        Assert.Equal("([Pay] == '12000')", packed.ToQuery());

        // A PackedCondition has to be unpacked before it can be built, so compare against what it unpacks to
        Assert.Equal(Matching(packed.Unpack()), Matching(RoundTrip(packed)));
    }

    // ---------- things that need quoting to survive ----------

    [Fact]
    public void AFieldNameThatIsNotAPlainIdentifierIsQuoted()
    {
        // A condition can name any field, whether or not a binding claims it, and bracket quoting cannot carry a
        // space, so the writer falls back to a quoted string which the parser accepts in field position. Note that
        // binding such a field is a separate matter: binding keys reject whitespace, see BindingKeyTests.
        var condition = new OneValueCondition<int>(Operator.Equals, "my field", 5);

        var written = condition.ToQuery();
        Assert.Equal("('my field' == 5)", written);

        var reparsed = Assert.IsType<OneValueCondition<string>>(ConditionFunctions.ParseQuery(written));
        Assert.Equal("my field", reparsed.Field);
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("Dotted.Path.Name")]
    [InlineData("with space")]
    [InlineData("with'quote")]
    [InlineData("with]bracket")]
    [InlineData("with,comma")]
    [InlineData("&&")]
    [InlineData("AND")]
    [InlineData("null")]
    public void AnyFieldNameRoundTrips(string field)
    {
        var condition = new OneValueCondition<int>(Operator.Equals, field, 5);

        var reparsed = Assert.IsType<OneValueCondition<string>>(ConditionFunctions.ParseQuery(condition.ToQuery()));

        Assert.Equal(field, reparsed.Field);
        Assert.Equal("5", reparsed.Value.Value);
    }

    /// <summary>
    /// A flags enum with more than one bit set formats as "Read, Write", which would tokenize as two values, so it
    /// has to be quoted on the way out.
    /// </summary>
    [Fact]
    public void AValueContainingASeparatorIsQuoted()
    {
        var condition = new OneValueCondition<FileAccessLike>(Operator.Equals, "Perm", FileAccessLike.Read | FileAccessLike.Write);

        var written = condition.ToQuery();
        Assert.Equal("([Perm] == 'Read, Write')", written);

        var reparsed = Assert.IsType<OneValueCondition<string>>(ConditionFunctions.ParseQuery(written));
        Assert.Equal("Read, Write", reparsed.Value.Value);
    }

    [Flags]
    public enum FileAccessLike
    {
        None = 0,
        Read = 1,
        Write = 2,
    }

    [Fact]
    public void AStringValueThatLooksLikeAKeywordStaysAValue()
    {
        foreach (var value in new[] { "null", "AND", "OR", "NOT", "&&", "IsNull", "true" })
        {
            var condition = new OneValueCondition<string>(Operator.Equals, nameof(Minion.Name), value);

            var reparsed = Assert.IsType<OneValueCondition<string>>(ConditionFunctions.ParseQuery(condition.ToQuery()));

            Assert.Equal(Operator.Equals, reparsed.Operator);
            Assert.Equal(value, reparsed.Value.Value);
        }
    }

    // ---------- what cannot be written, and says so ----------

    /// <summary>
    /// The query language has no literal for "match everything", so an empty conjunction cannot be round tripped.
    /// ToQuery says so rather than emitting "()", which is what ToString used to produce and the parser rejects.
    /// </summary>
    [Theory]
    [InlineData(Operator.And)]
    [InlineData(Operator.Or)]
    public void AnEmptyConjunctionCannotBeWrittenAndSaysSo(Operator op)
    {
        var condition = new ConjunctionCondition(op, []);

        Assert.Throws<WeequeryException>(() => condition.ToQuery());
    }

    [Fact]
    public void ToStringStillDescribesAnEmptyConjunctionInsteadOfThrowing()
    {
        // ToString backs debugging, so it must not throw even where ToQuery has to
        var condition = new ConjunctionCondition(Operator.And, []);

        Assert.Equal("(<empty And>)", condition.ToString());
    }

    [Fact]
    public void ToStringAndToQueryAgreeWhereverBothCanRender()
    {
        foreach (var query in Queries().Select(row => row.Data))
        {
            var condition = ConditionFunctions.ParseQuery(query)!;

            Assert.Equal(condition.ToQuery(), condition.ToString());
        }
    }
}
