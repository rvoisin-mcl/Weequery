using Tests.Common;
using Weequery;
using Weequery.Interfaces;

namespace Tests.Unit;

/// <summary>
/// The Native style: one spelling per operator, and the only style the parser is strict about.
/// <para>
/// Two halves to it. Writing, it settles on AND/OR/NOT and =/&lt;&gt;, with every named operator as its own name
/// in one word. Reading, it refuses the alternates the other two styles accept, and names the replacement while
/// doing so, since what is being refused worked for years.
/// </para>
/// <para>
/// The load bearing test here is <see cref="WhatNativeWritesNativeReads"/>. A style that emitted text its own
/// parser rejected would be worse than no style at all, and it is the one property that has to survive every
/// operator added later.
/// </para>
/// </summary>
public class NativeStyleTests
{
#pragma warning disable CS0618 // a deprecated style is how you ask for the permissive grammar, which is the point
    /// <summary>Read permissively, which now has to be asked for: the default is the strict grammar</summary>
    private static ICondition Parse(string query)
    {
        return ConditionFunctions.ParseQuery(query, QueryStyle.CSharp)!;
    }

    /// <summary>Read a sort clause permissively, which the default no longer does either</summary>
    private static List<Sort> ParseSorts(string clause)
    {
        return Sort.Parse(clause, null, QueryStyle.CSharp);
    }

    /// <summary>Read a combined string permissively, separator and condition both</summary>
    private static ParsedQuery ParseCombined(string query)
    {
        return ParsedQuery.Parse(query, null, QueryStyle.CSharp);
    }
#pragma warning restore CS0618

    /// <summary>Read under the strict grammar</summary>
    private static ICondition Strict(string query)
    {
        return ConditionFunctions.ParseQuery(query, QueryStyle.Native)!;
    }

    private static string Native(ICondition condition)
    {
        return condition.ToQuery(QueryStyle.Native);
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

    /// <summary>
    /// Every shape, in whatever spelling, since these are read permissively and it is the writing under test
    /// </summary>
    public static TheoryData<string> Queries()
    {
        return new TheoryData<string>(
            "Pay == 12000",
            "Pay != 12000",
            "Pay > 12000",
            "Pay <= 12000",
            "(Pay > 10000) && (IsActive == true)",
            "(Pay > 10000) || (IsActive == true)",
            "!(Pay > 10000)",
            "!!(Pay > 10000)",
            "!(Pay > 10000) && (IsActive == true)",
            "((Pay > 15000) || (Pay < 5000)) && (IsActive == true)",
            "(Pay > 1) && (Pay > 2) && (Pay > 3)",
            "Alias IsNull",
            "Alias IsNotNull",
            "Alias IS NULL",
            "Alias IS NOT NULL",
            "Pay IsBetween (8000, 12000)",
            "Pay IsNotBetween (8000, 12000)",
            "Pay BETWEEN 8000 AND 12000",
            "Pay NOT BETWEEN 8000 AND 12000",
            "Name IsIn ('Alice Fox', 'Bob Samuelson')",
            "Name IsNotIn ('Alice Fox')",
            "Name IN ('Alice Fox')",
            "Name NOT IN ('Alice Fox')",
            "Name StartsWith 'Al'",
            "Name DoesNotContain 'li'",
            "Pay > [Pay]",
            "Alias == null",
            "Alias != null");
    }

    // ---------- what it writes ----------

    [Fact]
    public void ItWritesWordsForTheConjunctions()
    {
        Assert.Equal("(([Pay] > '10000') AND ([IsActive] = 'true'))", Native(Parse("(Pay > 10000) && (IsActive == true)")));
        Assert.Equal("(([Pay] > '10000') OR ([IsActive] = 'true'))", Native(Parse("(Pay > 10000) || (IsActive == true)")));
        Assert.Equal("NOT ([Pay] > '10000')", Native(Parse("!(Pay > 10000)")));
    }

    [Fact]
    public void ItWritesTheSqlComparisonSymbols()
    {
        Assert.Equal("([Pay] = '12000')", Native(Parse("Pay == 12000")));
        Assert.Equal("([Pay] <> '12000')", Native(Parse("Pay != 12000")));
        Assert.Equal("([Pay] <> '12000')", Native(Parse("Pay <> 12000")));
    }

    /// <summary>
    /// Upper case, unlike the SQL style's And/Or/Not. Nothing depends on it, a keyword being read without regard
    /// to case, but it is what the style documents and what its examples show.
    /// </summary>
    [Fact]
    public void ItWritesTheConjunctionsInUpperCase()
    {
        Assert.Equal("AND", ConditionFunctions.GetOperationString(Operator.And, QueryStyle.Native));
        Assert.Equal("OR", ConditionFunctions.GetOperationString(Operator.Or, QueryStyle.Native));
        Assert.Equal("NOT", ConditionFunctions.GetOperationString(Operator.Not, QueryStyle.Native));
    }

    /// <summary>
    /// NOT needs the space that '!' did not, or a double negation runs into one word
    /// </summary>
    [Fact]
    public void ConsecutiveNotsStaySeparateWords()
    {
        Assert.Equal("NOT NOT ([Pay] > '10000')", Native(Parse("!!(Pay > 10000)")));
    }

    /// <summary>
    /// The named operators were already one word in every style, which is the half of Native that was true all
    /// along: IS NULL and IN were only ever accepted as input, never written.
    /// </summary>
    [Theory]
    [InlineData("Alias IS NULL", "IsNull")]
    [InlineData("Alias IS NOT NULL", "IsNotNull")]
    [InlineData("Pay BETWEEN 1 AND 2", "IsBetween")]
    [InlineData("Pay NOT BETWEEN 1 AND 2", "IsNotBetween")]
    [InlineData("Pay IN (1)", "IsIn")]
    [InlineData("Pay NOT IN (1)", "IsNotIn")]
    [InlineData("Name StartsWith 'A'", "StartsWith")]
    public void ItWritesEveryNamedOperatorAsItsOwnName(string query, string expected)
    {
        Assert.Contains(expected, Native(Parse(query)));
    }

    // ---------- the round trip that matters ----------

    /// <summary>
    /// Everything the Native style writes, the Native parser reads back, and to the same text again. An operator
    /// added later whose written form the strict grammar refuses fails here rather than in production.
    /// </summary>
    [Theory]
    [MemberData(nameof(Queries))]
    public void WhatNativeWritesNativeReads(string query)
    {
        var written = Native(Parse(query));

        Assert.Equal(written, Native(Strict(written)));
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public void TheNativeRoundTripSelectsTheSameRows(string query)
    {
        var condition = Parse(query);

        Assert.Equal(Matching(condition), Matching(Strict(Native(condition))));
    }

    /// <summary>
    /// Changing the spelling cannot change the answer, so the deprecated styles and this one have to agree about
    /// which rows a condition picks
    /// </summary>
    [Theory]
    [MemberData(nameof(Queries))]
    public void NativeAgreesWithTheDeprecatedStylesOnMeaning(string query)
    {
        var condition = Parse(query);

#pragma warning disable CS0618 // the deprecated style is the subject of the comparison
        var viaCSharp = Parse(condition.ToQuery(QueryStyle.CSharp));
#pragma warning restore CS0618

        Assert.Equal(Matching(viaCSharp), Matching(Parse(Native(condition))));
    }

    // ---------- what the strict parser refuses ----------

    /// <summary>
    /// Each of these is legal in the permissive grammar and refused in the strict one, with the replacement named.
    /// The message matters as much as the refusal, this being a spelling that used to work.
    /// </summary>
    [Theory]
    [InlineData("(Pay > 1) && (Pay > 2)", "&&", "AND")]
    [InlineData("(Pay > 1) || (Pay > 2)", "||", "OR")]
    [InlineData("!(Pay > 1)", "!", "NOT")]
    [InlineData("Alias IS NULL", "IS NULL", "IsNull")]
    [InlineData("Alias IS NOT NULL", "IS NOT NULL", "IsNotNull")]
    [InlineData("Pay NOT IN (1, 2)", "NOT IN", "IsNotIn")]
    [InlineData("Pay NOT BETWEEN (1, 2)", "NOT BETWEEN", "IsNotBetween")]
    public void ARefusedSpellingSaysWhatToWriteInstead(string query, string found, string instead)
    {
        // The permissive grammar takes it, which is the point: none of these is malformed
        Assert.NotNull(Parse(query));

        var error = Assert.Throws<WeequeryException>(() => Strict(query));

        Assert.Contains(found, error.Message);
        Assert.Contains(instead, error.Message);
        Assert.Equal(WeequeryError.QuerySyntax, error.Error);
    }

    /// <summary>
    /// The infix range. Legal in the permissive grammar, where the first AND is the separator and a second would
    /// be the conjunction; refused here, because a style whose point is that nothing reads two ways cannot keep a
    /// word that means two things three tokens apart.
    /// </summary>
    [Theory]
    [InlineData("Pay IsBetween 8000 AND 12000")]
    [InlineData("Pay IsNotBetween 8000 AND 12000")]
    [InlineData("Pay BETWEEN 8000 AND 12000")]
    public void ARangeWrittenWithAnInfixAndIsRefused(string query)
    {
        Assert.NotNull(Parse(query));

        var error = Assert.Throws<WeequeryException>(() => Strict(query));

        Assert.Contains("(low, high)", error.Message);
    }

    /// <summary>
    /// BETWEEN is read as the operator and still cannot bring SQL's range syntax with it, so the two halves of
    /// that decision do not contradict each other: the word is fine, the infix AND is not.
    /// </summary>
    [Fact]
    public void BetweenIsReadButItsRangeSyntaxIsNot()
    {
        Assert.Equal("([Pay] IsBetween ('1', '2'))", Native(Strict("Pay BETWEEN (1, 2)")));
        Assert.Throws<WeequeryException>(() => Strict("Pay BETWEEN 1 AND 2"));
    }

    // ---------- the sort clause prefix ----------

    /// <summary>
    /// The rule about a name being one word is not one the separator gets to be exempt from for being a
    /// separator. Both spellings still read under every other style.
    /// </summary>
    [Theory]
    [InlineData("ORDER BY Pay DESC")]
    [InlineData("order by Pay DESC")]
    public void TheTwoWordSortPrefixIsRefused(string clause)
    {
        Assert.NotEmpty(ParseSorts(clause));

        var error = Assert.Throws<WeequeryException>(() => Sort.Parse(clause, null, QueryStyle.Native));

        Assert.Contains("ORDER BY", error.Message);
        Assert.Equal(WeequeryError.QuerySyntax, error.Error);
    }

    [Theory]
    [InlineData("OrderBy Pay DESC")]
    [InlineData("orderby Pay DESC")]
    [InlineData("Pay DESC")]
    [InlineData("Pay DESC, Name")]
    public void TheOneWordPrefixAndNoPrefixAtAllAreAccepted(string clause)
    {
        Assert.NotEmpty(Sort.Parse(clause, null, QueryStyle.Native));
    }

    /// <summary>
    /// A field genuinely named Order still reads, since it is only the two words together that were ever the
    /// prefix and the refusal is on the pair
    /// </summary>
    [Fact]
    public void AFieldNamedOrderStillReads()
    {
        var sorts = Sort.Parse("Order DESC", null, QueryStyle.Native);

        Assert.Equal("Order", Assert.Single(sorts).Field);
    }

    [Fact]
    public void ACombinedStringIsRefusedForItsSeparatorToo()
    {
        Assert.NotNull(ParseCombined("Pay > 1 ORDER BY Pay"));

        var error = Assert.Throws<WeequeryException>(() => ParsedQuery.Parse("Pay > 1 ORDER BY Pay", null, QueryStyle.Native));

        Assert.Contains("ORDER BY", error.Message);

        // the one word spelling goes through, and so does a clause with no condition in front of it
        Assert.NotNull(ParsedQuery.Parse("Pay > 1 OrderBy Pay", null, QueryStyle.Native).Condition);
        Assert.Single(ParsedQuery.Parse("OrderBy Pay", null, QueryStyle.Native).Sorts);
        Assert.Throws<WeequeryException>(() => ParsedQuery.Parse("ORDER BY Pay", null, QueryStyle.Native));
    }

    [Fact]
    public void ApplySortsTakesTheStyle()
    {
        var inquiry = MinionTestData.Minions().WithWeequery().BindProperties(Minion.Bindings);

        Assert.Throws<WeequeryException>(() => inquiry.ApplySorts("ORDER BY Pay DESC", null, QueryStyle.Native));

        Assert.Equal(["Charlie", "Alice", "David", "Bob"], MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplySorts("OrderBy Pay DESC", null, QueryStyle.Native)
            .Build()
            .ToList()
            .Select(minion => minion.Name.Split(' ')[0])
            .ToArray());
    }

    /// <summary>
    /// A value spelling the separator is a value, the same as one spelling a refused operator. The refusal is on
    /// the prefix, and a quoted literal is never read as one.
    /// </summary>
    [Fact]
    public void AValueSpellingTheSeparatorIsStillAValue()
    {
        var parsed = ParsedQuery.Parse("Name = 'ORDER BY'", null, QueryStyle.Native);

        Assert.Equal("([Name] = 'ORDER BY')", parsed.Condition!.ToQuery(QueryStyle.Native));
        Assert.Empty(parsed.Sorts);
    }

    // ---------- what the strict parser still takes ----------

    [Theory]
    [InlineData("(Pay > 1) AND (Pay > 2)")]
    [InlineData("(Pay > 1) OR (Pay > 2)")]
    [InlineData("NOT (Pay > 1)")]
    [InlineData("NOT NOT (Pay > 1)")]
    [InlineData("Pay = 1")]
    [InlineData("Pay == 1")]
    [InlineData("Pay <> 1")]
    [InlineData("Pay IN (1, 2)")]
    [InlineData("Pay BETWEEN (1, 2)")]
    [InlineData("Pay >= 1")]
    [InlineData("Pay <= 1")]
    [InlineData("Alias IsNull")]
    [InlineData("Alias IsNotNull")]
    [InlineData("Pay IsIn (1, 2)")]
    [InlineData("Pay IsNotIn (1, 2)")]
    [InlineData("Pay IsBetween (1, 2)")]
    [InlineData("Pay IsNotBetween (1, 2)")]
    [InlineData("Name StartsWith 'A'")]
    [InlineData("Name DoesNotContain 'A'")]
    [InlineData("Pay > [Pay]")]
    [InlineData("Alias = null")]
    [InlineData("Alias <> null")]
    public void TheOneSpellingOfEachOperatorIsAccepted(string query)
    {
        Assert.NotNull(Strict(query));
    }

    /// <summary>
    /// The alternates Native keeps, and what each is written back out as. A spelling that is already one word
    /// breaks no rule by also being an alternate, so the strict grammar reads it; the writer is what settles on
    /// one form, and that is where two spellings become one.
    /// </summary>
    [Theory]
    [InlineData("Pay == 1", "([Pay] = '1')")]
    [InlineData("Pay = 1", "([Pay] = '1')")]
    [InlineData("Pay != 1", "([Pay] <> '1')")]
    [InlineData("Pay <> 1", "([Pay] <> '1')")]
    [InlineData("Pay IN (1, 2)", "([Pay] IsIn ('1', '2'))")]
    [InlineData("Pay IsIn (1, 2)", "([Pay] IsIn ('1', '2'))")]
    [InlineData("Pay BETWEEN (1, 2)", "([Pay] IsBetween ('1', '2'))")]
    [InlineData("Pay IsBetween (1, 2)", "([Pay] IsBetween ('1', '2'))")]
    public void AOneWordAlternateIsReadAndWrittenBackAsTheOneForm(string query, string written)
    {
        Assert.Equal(written, Native(Strict(query)));
    }

    /// <summary>
    /// The negatives of the two that are kept are two words each, so they go the other way. This is the line: it
    /// is drawn at the space, not at whether the spelling came from SQL.
    /// </summary>
    [Fact]
    public void TheTwoWordNegativesOfTheKeptAlternatesAreStillRefused()
    {
        Assert.Throws<WeequeryException>(() => Strict("Pay NOT IN (1, 2)"));
        Assert.Throws<WeequeryException>(() => Strict("Pay NOT BETWEEN (1, 2)"));

        // and '!' on its own is still refused, though '!=' is not, so the carve out is per spelling
        Assert.Throws<WeequeryException>(() => Strict("!(Pay = 1)"));
        Assert.NotNull(Strict("Pay != 1"));
    }

    /// <summary>
    /// Strictness is about alternate spellings, not about casing. A keyword has always been matched without regard
    /// to case and still is.
    /// </summary>
    [Theory]
    [InlineData("(Pay > 1) and (Pay > 2)")]
    [InlineData("(Pay > 1) Or (Pay > 2)")]
    [InlineData("nOt (Pay > 1)")]
    [InlineData("Alias isnull")]
    [InlineData("Pay ISIN (1, 2)")]
    [InlineData("Pay isBetween (1, 2)")]
    public void CaseStillDoesNotMatter(string query)
    {
        Assert.NotNull(Strict(query));
    }

    /// <summary>
    /// A value that spells a refused operator is a value. The refusals are on operators, and a quoted literal is
    /// never read as one.
    /// </summary>
    [Fact]
    public void AValueSpellingARefusedOperatorIsStillAValue()
    {
        Assert.Equal("([Name] = '&&')", Native(Strict("Name = '&&'")));
        Assert.Equal("([Name] = 'IS NULL')", Native(Strict("Name = 'IS NULL'")));
    }

    // ---------- reachable from the places a query actually arrives ----------

    private static string[] NamesFrom(string query, QueryStyle style)
    {
        return MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition(query, style)
            .Build()
            .ToList()
            .Select(minion => minion.Name.Split(' ')[0])
            .Order()
            .ToArray();
    }

    [Fact]
    public void ApplyConditionTakesTheStyle()
    {
        var inquiry = MinionTestData.Minions().WithWeequery().BindProperties(Minion.Bindings);

        Assert.Throws<WeequeryException>(() => inquiry.ApplyCondition("(Pay > 1) && (IsActive = true)", QueryStyle.Native));

        // the same question in the one spelling goes through, and the older spelling still reads when it is asked for
        Assert.Equal(["Alice", "David"], NamesFrom("(Pay > 1) AND (IsActive = true)", QueryStyle.Native));
        Assert.Equal(["Alice", "David"], NamesFrom("(Pay > 1) && (IsActive == true)", QueryStyle.CSharp));
    }

    [Fact]
    public void ParsedQueryTakesTheStyleForItsConditionHalf()
    {
        Assert.Throws<WeequeryException>(() => ParsedQuery.Parse("(Pay > 1) && (Pay < 2) OrderBy Pay", null, QueryStyle.Native));

        var parsed = ParsedQuery.Parse("(Pay > 1) AND (Pay < 2) OrderBy Pay", null, QueryStyle.Native);

        Assert.NotNull(parsed.Condition);
        Assert.Single(parsed.Sorts);
    }

    [Fact]
    public void ATransportConditionTakesTheStyleForItsQueryHalf()
    {
        Assert.Throws<WeequeryException>(() => new TransportCondition("(Pay > 1) && (Pay < 2)").Unpack(QueryStyle.Native));

        Assert.NotNull(new TransportCondition("(Pay > 1) AND (Pay < 2)").Unpack(QueryStyle.Native));
    }

    /// <summary>
    /// The separator between the two halves of a combined string is spelled as one word in this style, for the
    /// same reason the operators are. Both spellings still read.
    /// </summary>
    [Fact]
    public void ACombinedStringWritesTheOneWordSeparator()
    {
        var parsed = ParseCombined("Pay > 10000 ORDER BY Pay DESC");

        Assert.Equal("([Pay] > '10000') OrderBy [Pay] DESC", parsed.ToQuery(QueryStyle.Native));

        // and reads back, under the strict grammar, to the same thing
        Assert.Equal(
            parsed.ToQuery(QueryStyle.Native),
            ParsedQuery.Parse(parsed.ToQuery(QueryStyle.Native), null, QueryStyle.Native).ToQuery(QueryStyle.Native));
    }

    /// <summary>
    /// A bare sort clause needs no prefix to read back, so this style does not add one. Only the SQL style does,
    /// and only because that is what it is for.
    /// </summary>
    [Fact]
    public void ABareSortClauseIsStillWrittenWithoutAPrefix()
    {
        var sorts = Sort.Parse("Pay DESC, Name", null);

        Assert.Equal("[Pay] DESC, [Name] ASC", sorts.ToQuery(QueryStyle.Native));
        Assert.Equal(sorts.ToQuery(QueryStyle.Native), sorts.ToQuery());
    }
}
