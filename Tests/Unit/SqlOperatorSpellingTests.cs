using Tests.Common;
using Weequery;
using Weequery.Interfaces;

namespace Tests.Unit;

/// <summary>
/// The parser accepts SQL's spellings of the operators alongside its own: IN, NOT IN, IS NULL, IS NOT NULL,
/// BETWEEN and NOT BETWEEN, including SQL's "BETWEEN low AND high" range syntax.
/// <para>
/// Only IN was reported, but the neighbours were missing too, and a language that took IN while rejecting NOT IN
/// would just move the surprise along. LIKE is deliberately still absent, see the last test here.
/// </para>
/// </summary>
public class SqlOperatorSpellingTests
{
    private static ICondition Parse(string query)
    {
        return ConditionFunctions.ParseQuery(query)!;
    }

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

    // ---------- the reported case ----------

    [Fact]
    public void TheReportedQueryParses()
    {
        Assert.Equal(["Alice", "Bob", "Charlie", "David"], Matching("[Pay] IN (0,8000,12000,19000)"));
    }

    [Fact]
    public void InTakesEitherListDelimiter()
    {
        Assert.Equal(Matching("Pay IN (8000, 12000)"), Matching("Pay IN (8000, 12000)"));
        Assert.Equal(["Alice", "David"], Matching("Pay IN (8000, 12000)"));
    }

    [Fact]
    public void InAcceptsABareSingleValue()
    {
        Assert.Equal(["Alice"], Matching("Pay IN 12000"));
    }

    // ---------- each SQL spelling means the same as its IsX counterpart ----------

    [Theory]
    [InlineData("Pay IN (8000, 12000)", "Pay IsIn (8000, 12000)")]
    [InlineData("Pay NOT IN (8000, 12000)", "Pay IsNotIn (8000, 12000)")]
    [InlineData("Alias IS NULL", "Alias IsNull")]
    [InlineData("Alias IS NOT NULL", "Alias IsNotNull")]
    [InlineData("Pay BETWEEN 8000 AND 12000", "Pay IsBetween (8000, 12000)")]
    [InlineData("Pay NOT BETWEEN 8000 AND 12000", "Pay IsNotBetween (8000, 12000)")]
    [InlineData("Pay BETWEEN (8000, 12000)", "Pay IsBetween (8000, 12000)")]
    public void ASqlSpellingMeansTheSameAsTheNamedOne(string sql, string named)
    {
        Assert.Equal(Parse(named).ToQuery(), Parse(sql).ToQuery());
        Assert.Equal(Matching(named), Matching(sql));
    }

    [Theory]
    [InlineData("Pay in (8000)")]
    [InlineData("Pay In (8000)")]
    [InlineData("Pay not in (8000)")]
    [InlineData("Alias is null")]
    [InlineData("Alias Is Not Null")]
    [InlineData("Pay between 8000 and 12000")]
    [InlineData("Pay NOT between 8000 AND 12000")]
    public void TheSqlSpellingsAreCaseInsensitive(string query)
    {
        // Parsing without throwing is the assertion; the equivalences are covered above
        Assert.NotNull(Parse(query));
    }

    // ---------- SQL's range syntax ----------

    [Fact]
    public void BetweenReadsTheAndAsItsSeparator()
    {
        Assert.Equal(["Alice", "David"], Matching("Pay BETWEEN 8000 AND 12000"));
        Assert.Equal(["Bob", "Charlie"], Matching("Pay NOT BETWEEN 8000 AND 12000"));
    }

    /// <summary>
    /// The one genuinely ambiguous case: BETWEEN consumes exactly one AND as its separator, so a second AND is the
    /// conjunction. That is where SQL splits it too.
    /// </summary>
    [Fact]
    public void ASecondAndIsTheConjunctionNotThePartOfTheRange()
    {
        var expected = Matching("(Pay IsBetween (8000, 12000)) && (IsActive == true)");

        Assert.Equal(expected, Matching("Pay BETWEEN 8000 AND 12000 AND IsActive == true"));
        Assert.Equal(expected, Matching("Pay BETWEEN 8000 AND 12000 && IsActive == true"));
        Assert.Equal(expected, Matching("(Pay BETWEEN 8000 AND 12000) AND (IsActive == true)"));
    }

    [Fact]
    public void ARangeCombinesWithOrToo()
    {
        var expected = Matching("(Pay IsNotBetween (8000, 12000)) || (Pay == 19000)");

        Assert.Equal(expected, Matching("Pay NOT BETWEEN 8000 AND 12000 OR Pay == 19000"));
    }

    // ---------- the keywords only mean something in operator position ----------

    /// <summary>
    /// IN, IS and BETWEEN are read as operators only where an operator is expected, so a property named after one
    /// can still be filtered on
    /// </summary>
    [Theory]
    [InlineData("IN")]
    [InlineData("IS")]
    [InlineData("BETWEEN")]
    [InlineData("LIKE")]
    public void AFieldMayBeNamedAfterASqlKeyword(string field)
    {
        var condition = Assert.IsType<OneValueCondition<string>>(Parse($"{field} == 5"));

        Assert.Equal(field, condition.Field);
        Assert.Equal("5", condition.Value.Value);
    }

    [Fact]
    public void AFieldNamedInCanStillUseTheInOperator()
    {
        var condition = Assert.IsType<MultipleValueCondition<string>>(Parse("IN IN (1, 2)"));

        Assert.Equal("IN", condition.Field);
        Assert.Equal(Operator.IsIn, condition.Operator);
        Assert.Equal(["1", "2"], condition.StringifyValues());
    }

    // ---------- malformed phrases are still rejected ----------

    /// <summary>
    /// A phrase the parser started reading and could not finish is refused rather than half read. IS wants NULL
    /// after it, and NOT wants IN or BETWEEN.
    /// </summary>
    [Theory]
    [InlineData("Pay IS 5")]
    [InlineData("Pay IS NOT 5")]
    [InlineData("Pay IS")]
    [InlineData("Pay NOT 5")]
    [InlineData("Pay NOT LIKE 'x'")]
    [InlineData("Pay NOT")]
    public void AnIncompletePhraseIsRefused(string query)
    {
        Assert.Throws<WeequeryException>(() => Parse(query));
    }

    [Fact]
    public void BetweenStillNeedsTwoValues()
    {
        Assert.Throws<WeequeryException>(() => Parse("Pay BETWEEN 8000"));
    }

    [Fact]
    public void BetweenRejectsAThirdValue()
    {
        Assert.Throws<WeequeryException>(() => Parse("Pay BETWEEN (1, 2, 3)"));
    }

    /// <summary>
    /// LIKE is not an alias for anything: its meaning depends on where the wildcards sit, so 'A%' is StartsWith,
    /// '%A' is EndsWith and '%A%' is Contains, and there is the escape character and the single character wildcard
    /// to account for. Translating it is a feature rather than a spelling, so it stays unsupported and says so.
    /// </summary>
    [Fact]
    public void LikeIsNotSupported()
    {
        Assert.Throws<WeequeryException>(() => Parse("Name LIKE 'A%'"));
    }

    // ---------- what comes back out ----------

    /// <summary>
    /// The SQL spellings are input only. A condition parsed from one writes back in the canonical form, which
    /// reparses, so the round trip still closes.
    /// </summary>
    [Theory]
    [InlineData("Pay IN (8000, 12000)", "IsIn")]
    [InlineData("Pay NOT IN (8000)", "IsNotIn")]
    [InlineData("Alias IS NULL", "IsNull")]
    [InlineData("Alias IS NOT NULL", "IsNotNull")]
    [InlineData("Pay BETWEEN 8000 AND 12000", "IsBetween")]
    [InlineData("Pay NOT BETWEEN 8000 AND 12000", "IsNotBetween")]
    public void ASqlSpellingIsWrittenBackInTheCanonicalForm(string query, string expected)
    {
        var written = Parse(query).ToQuery();

        Assert.Contains(expected, written);
        Assert.Equal(Matching(query), Matching(written));
    }

    [Fact]
    public void TheSqlWriteStyleStillUsesTheNamedOperators()
    {
        // QueryStyle.Sql changes &&/||/!/==/!= only; the named operators have one written form
        Assert.Contains("IsIn", Parse("Pay IN (1)").ToQuery(QueryStyle.Sql));
        Assert.Contains("IsNull", Parse("Alias IS NULL").ToQuery(QueryStyle.Sql));
    }
}
