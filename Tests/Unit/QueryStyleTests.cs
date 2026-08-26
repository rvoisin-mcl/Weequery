using System.Text;
using Tests.Common;
using Weequery;
using Weequery.Interfaces;

namespace Tests.Unit;

/// <summary>
/// ToQuery can write the operators that have two spellings either way: C# (&amp;&amp;, ||, !, ==, !=) or SQL
/// (And, Or, Not, =, &lt;&gt;).
/// <para>
/// The parser accepts both, so the choice only affects what comes out. Both styles must round trip and must select
/// the same rows, which is what most of these check.
/// </para>
/// <para>
/// Nothing here pins the case of an operator spelled as a word. The parser reads one without regard to case, so
/// which case the writer picks is a matter of how the text looks rather than of what it means, and a test that
/// asserted one would fail on a change that broke nothing. The assertions that name a keyword therefore compare
/// without regard to case; that the case genuinely does not matter is checked by
/// <see cref="TheSqlOperatorsAreReadWithoutRegardToCase"/> and
/// <see cref="EveryShapeInTheSqlStyleReadsBackLowered"/>.
/// </para>
/// </summary>
public class QueryStyleTests
{
    private static ICondition Parse(string query)
    {
        return ConditionFunctions.ParseQuery(query)!;
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
    /// Every shape where the two styles differ, plus a few where they should not
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
            "Pay IsBetween (8000, 12000)",
            "Pay IsNotBetween (8000, 12000)",
            "Name IsIn ('Alice Fox', 'Bob Samuelson')",
            "Name IsNotIn ('Alice Fox')",
            "Name StartsWith 'Al'",
            "Name DoesNotContain 'li'",
            "Alias == null",
            "Alias != null");
    }

    // ---------- the spelling that comes out ----------

    [Fact]
    public void TheCSharpStyleUsesSymbols()
    {
        Assert.Equal("(([Pay] > '10000') && ([IsActive] == 'true'))", Parse("(Pay > 10000) && (IsActive == true)").ToQuery(QueryStyle.CSharp));
        Assert.Equal("(([Pay] > '10000') || ([IsActive] == 'true'))", Parse("(Pay > 10000) || (IsActive == true)").ToQuery(QueryStyle.CSharp));
        Assert.Equal("!([Pay] > '10000')", Parse("!(Pay > 10000)").ToQuery(QueryStyle.CSharp));
        Assert.Equal("([Pay] == '12000')", Parse("Pay == 12000").ToQuery(QueryStyle.CSharp));
        Assert.Equal("([Pay] != '12000')", Parse("Pay != 12000").ToQuery(QueryStyle.CSharp));
    }

    /// <summary>
    /// The conjunctions come out as words rather than symbols. Which case they are written in is not the subject,
    /// see the remarks on the class.
    /// </summary>
    [Fact]
    public void TheSqlStyleUsesKeywords()
    {
        Assert.Equal("(([Pay] > '10000') AND ([IsActive] = 'true'))", Parse("(Pay > 10000) && (IsActive == true)").ToQuery(QueryStyle.Sql), ignoreCase: true);
        Assert.Equal("(([Pay] > '10000') OR ([IsActive] = 'true'))", Parse("(Pay > 10000) || (IsActive == true)").ToQuery(QueryStyle.Sql), ignoreCase: true);
        Assert.Equal("NOT ([Pay] > '10000')", Parse("!(Pay > 10000)").ToQuery(QueryStyle.Sql), ignoreCase: true);

        // Symbols either way, so these are exact
        Assert.Equal("([Pay] = '12000')", Parse("Pay == 12000").ToQuery(QueryStyle.Sql));
        Assert.Equal("([Pay] <> '12000')", Parse("Pay != 12000").ToQuery(QueryStyle.Sql));
    }

    [Fact]
    public void CSharpIsTheDefault()
    {
        var condition = Parse("(Pay > 10000) && (IsActive == true)");

        Assert.Equal(condition.ToQuery(QueryStyle.CSharp), condition.ToQuery());
    }

    [Fact]
    public void ToStringKeepsTheCSharpStyle()
    {
        // ToString cannot take an argument, so it stays on the default
        var condition = Parse("!(Pay > 10000) && (IsActive == true)");

        Assert.Equal(condition.ToQuery(QueryStyle.CSharp), condition.ToString());
    }

    /// <summary>
    /// A double negation must not run the two keywords together
    /// </summary>
    [Fact]
    public void ConsecutiveNotsStaySeparateWords()
    {
        Assert.Equal("NOT NOT ([Pay] > '10000')", Parse("!!(Pay > 10000)").ToQuery(QueryStyle.Sql), ignoreCase: true);
        Assert.Equal("!!([Pay] > '10000')", Parse("!!(Pay > 10000)").ToQuery(QueryStyle.CSharp));
    }

    /// <summary>
    /// The named operators have one spelling each, because that is the only form the parser reads. The style must
    /// not turn IsNull into "IS NULL" or IsIn into "IN", which would produce unparseable text.
    /// </summary>
    [Theory]
    [InlineData("Alias IsNull", "IsNull")]
    [InlineData("Alias IsNotNull", "IsNotNull")]
    [InlineData("Pay IsBetween (1, 2)", "IsBetween")]
    [InlineData("Pay IsNotBetween (1, 2)", "IsNotBetween")]
    [InlineData("Pay IsIn (1)", "IsIn")]
    [InlineData("Pay IsNotIn (1)", "IsNotIn")]
    [InlineData("Name StartsWith 'A'", "StartsWith")]
    [InlineData("Name DoesNotContain 'A'", "DoesNotContain")]
    [InlineData("Pay > 1", ">")]
    [InlineData("Pay <= 1", "<=")]
    public void TheNamedAndOrderingOperatorsAreTheSameInBothStyles(string query, string expected)
    {
        var condition = Parse(query);

        Assert.Contains(expected, condition.ToQuery(QueryStyle.CSharp));
        Assert.Contains(expected, condition.ToQuery(QueryStyle.Sql));
    }

    // ---------- both styles read back ----------

    [Theory]
    [MemberData(nameof(Queries))]
    public void BothStylesReparse(string query)
    {
        var condition = Parse(query);

        Assert.NotNull(ConditionFunctions.ParseQuery(condition.ToQuery(QueryStyle.CSharp)));
        Assert.NotNull(ConditionFunctions.ParseQuery(condition.ToQuery(QueryStyle.Sql)));
    }

    /// <summary>
    /// The SQL style spells its operators as words, so the case they are written in cannot be part of what they
    /// mean. The C# style has nothing to check here, its operators being symbols.
    /// </summary>
    [Theory]
    [InlineData("(Pay > 10000) and (IsActive == true)", "(Pay > 10000) AND (IsActive == true)")]
    [InlineData("(Pay > 10000) Or (IsActive == true)", "(Pay > 10000) OR (IsActive == true)")]
    [InlineData("not (Pay > 10000)", "NOT (Pay > 10000)")]
    [InlineData("nOt (Pay > 10000) aNd (IsActive == true)", "NOT (Pay > 10000) AND (IsActive == true)")]
    [InlineData("Alias is null", "Alias IS NULL")]
    [InlineData("Alias Is Not Null", "Alias IS NOT NULL")]
    [InlineData("Pay in (8000, 12000)", "Pay IN (8000, 12000)")]
    [InlineData("Pay not in (8000, 12000)", "Pay NOT IN (8000, 12000)")]
    [InlineData("Pay between 8000 and 12000", "Pay BETWEEN 8000 AND 12000")]
    [InlineData("Pay Not Between 8000 And 12000", "Pay NOT BETWEEN 8000 AND 12000")]
    public void TheSqlOperatorsAreReadWithoutRegardToCase(string cased, string canonical)
    {
        Assert.Equal(Parse(canonical).ToQuery(QueryStyle.Sql), Parse(cased).ToQuery(QueryStyle.Sql));
    }

    /// <summary>
    /// The same across every shape, and for the named operators the two styles share: what ToQuery writes in the
    /// SQL style reads back as the same condition with every operator lowered. An operator added later without
    /// case handling fails here rather than only for the queries someone thought to list above.
    /// </summary>
    [Theory]
    [MemberData(nameof(Queries))]
    public void EveryShapeInTheSqlStyleReadsBackLowered(string query)
    {
        var written = Parse(query).ToQuery(QueryStyle.Sql);

        Assert.Equal(written, Parse(Lowered(written)).ToQuery(QueryStyle.Sql));
    }

    /// <summary>
    /// A written query with its operators lowered and nothing else touched. Everything the writer emits is either
    /// a bracketed field, a quoted value, or an operator, so anything outside brackets and quotes is one.
    /// </summary>
    private static string Lowered(string written)
    {
        var text = new StringBuilder(written.Length);
        var quoted = false;
        var bracketed = false;

        for (var index = 0; index < written.Length; index++)
        {
            var character = written[index];

            if (quoted)
            {
                text.Append(character);

                // An escaped character cannot close the quote, so step over it
                if ((character == '\\') && ((index + 1) < written.Length)) { text.Append(written[++index]); }
                else if (character == '\'') { quoted = false; }

                continue;
            }

            if (character == '\'') { quoted = true; }
            else if (character == '[') { bracketed = true; }
            else if (character == ']') { bracketed = false; }

            text.Append(bracketed ? character : char.ToLowerInvariant(character));
        }

        return text.ToString();
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public void BothStylesSelectTheSameRows(string query)
    {
        var condition = Parse(query);

        var viaCSharp = Parse(condition.ToQuery(QueryStyle.CSharp));
        var viaSql = Parse(condition.ToQuery(QueryStyle.Sql));

        Assert.Equal(Matching(condition), Matching(viaCSharp));
        Assert.Equal(Matching(condition), Matching(viaSql));
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public void EachStyleIsStableOnceParsed(string query)
    {
        var condition = Parse(query);

        foreach (var style in new[] { QueryStyle.CSharp, QueryStyle.Sql })
        {
            var once = condition.ToQuery(style);
            var twice = Parse(once).ToQuery(style);

            Assert.Equal(once, twice);
        }
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public void TheTwoStylesParseToTheSameTree(string query)
    {
        var condition = Parse(query);

        // Re-writing each in the other style must converge, so the parse of one equals the parse of the other
        var fromCSharp = Parse(condition.ToQuery(QueryStyle.CSharp));
        var fromSql = Parse(condition.ToQuery(QueryStyle.Sql));

        Assert.Equal(fromCSharp.ToQuery(QueryStyle.Sql), fromSql.ToQuery(QueryStyle.Sql));
    }

    // ---------- the operator string helper ----------

    [Theory]
    [InlineData(Operator.Equals, "==", "=")]
    [InlineData(Operator.NotEqual, "!=", "<>")]
    [InlineData(Operator.And, "&&", "AND")]
    [InlineData(Operator.Or, "||", "OR")]
    [InlineData(Operator.Not, "!", "NOT")]
    public void GetOperationStringHonoursTheStyle(Operator op, string csharp, string sql)
    {
        Assert.Equal(csharp, ConditionFunctions.GetOperationString(op, QueryStyle.CSharp));
        Assert.Equal(sql, ConditionFunctions.GetOperationString(op, QueryStyle.Sql), ignoreCase: true);
    }

    [Theory]
    [InlineData(Operator.IsNull)]
    [InlineData(Operator.IsIn)]
    [InlineData(Operator.IsBetween)]
    [InlineData(Operator.StartsWith)]
    [InlineData(Operator.GreaterThan)]
    [InlineData(Operator.LessThanOrEqual)]
    public void GetOperationStringIsTheSameForOperatorsWithOneSpelling(Operator op)
    {
        Assert.Equal(
            ConditionFunctions.GetOperationString(op, QueryStyle.CSharp),
            ConditionFunctions.GetOperationString(op, QueryStyle.Sql));
    }

    [Fact]
    public void TheSingleArgumentOverloadStillGivesTheCSharpSpelling()
    {
        // Kept as it was, so existing callers are unaffected
        Assert.Equal("==", ConditionFunctions.GetOperationString(Operator.Equals));
        Assert.Equal("!=", ConditionFunctions.GetOperationString(Operator.NotEqual));
    }

    // ---------- a hand built tree, in both styles ----------

    [Fact]
    public void AHandBuiltTreeWritesInBothStyles()
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

        Assert.Equal("(!([Pay] > 10000) && (([IsActive] == True) || ([Alias] IsNull)))", condition.ToQuery(QueryStyle.CSharp));
        Assert.Equal("(NOT ([Pay] > 10000) AND (([IsActive] = True) OR ([Alias] IsNull)))", condition.ToQuery(QueryStyle.Sql), ignoreCase: true);

        Assert.Equal(Matching(condition), Matching(Parse(condition.ToQuery(QueryStyle.Sql))));
    }
}
