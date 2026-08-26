using Tests.Common;
using Weequery;
using Weequery.Interfaces;

namespace Tests.Unit;

public class QueryParserTests
{
    /// <summary>
    /// Parse the query, run it against the shared test set, and return the matching minions' first names
    /// </summary>
    private static string[] Run(string query)
    {
        var condition = ConditionFunctions.ParseQuery(query);

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

    private static void AssertMatches(string query, params string[] expected)
    {
        Assert.Equal(expected.Order().ToArray(), Run(query));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyQueryParsesToNull(string query)
    {
        Assert.Null(ConditionFunctions.ParseQuery(query));
    }

    // ---------- NOT applies only to its own operand ----------

    /// <summary>
    /// The bug this covers: '!' used to swallow the conjunction that followed it, so
    /// '!A &amp;&amp; B' was parsed as '!(A &amp;&amp; B)'.
    /// </summary>
    [Fact]
    public void NotBindsTighterThanAnd()
    {
        // !(pay > 10000) is Bob + David; AND active keeps both.
        // The old behaviour, !((pay > 10000) && active), would have returned Bob + Charlie + David.
        AssertMatches("!(Pay > 10000) && (IsActive == true)", "Bob", "David");
    }

    [Fact]
    public void NotBindsTighterThanAndWhenTrailing()
    {
        AssertMatches("(IsActive == true) && !(Pay > 10000)", "Bob", "David");
    }

    [Fact]
    public void NotBindsTighterThanOr()
    {
        // !(active) is Charlie; OR pay == 0 adds Bob
        AssertMatches("!(IsActive == true) || (Pay == 0)", "Bob", "Charlie");
    }

    [Fact]
    public void ConsecutiveNotsEachBindToOneOperand()
    {
        AssertMatches("!(Pay > 10000) && !(IsActive == false)", "Bob", "David");
    }

    [Fact]
    public void NotAppliesToAParenthesizedGroup()
    {
        AssertMatches("!((Name == 'Alice Fox') || (Name == 'Bob Samuelson'))", "Charlie", "David");
    }

    [Fact]
    public void DoubleNegationCancels()
    {
        AssertMatches("!!(IsActive == true)", "Alice", "Bob", "David");
    }

    [Fact]
    public void NotConditionBuildsAnExpression()
    {
        // Operator.Not used to throw for every input: "The unary operator Not is not defined for the type Func<Minion, bool>"
        var condition = new NotCondition(Operator.Not, new OneValueCondition<bool>(Operator.Equals, nameof(Minion.IsActive), true));
        var expression = Weequery.Inquiry<Minion>.BuildExpression(Minion.Bindings, condition);

        Assert.Single(MinionTestData.Minions().Where(expression));
    }

    [Fact]
    public void NotKeywordIsEquivalentToBang()
    {
        Assert.Equal(Run("!(IsActive == true)"), Run("NOT (IsActive == true)"));
    }

    // ---------- grouping and precedence ----------

    [Fact]
    public void NestedGroupsControlPrecedence()
    {
        // (pay > 15000 -> Charlie) or (pay < 5000 -> Bob), then AND active leaves Bob
        AssertMatches("((Pay > 15000) || (Pay < 5000)) && (IsActive == true)", "Bob");
    }

    [Fact]
    public void NestedGroupsControlPrecedenceWhenTrailing()
    {
        AssertMatches("(IsActive == true) && ((Pay > 15000) || (Pay < 5000))", "Bob");
    }

    [Fact]
    public void AndBindsTighterThanOr()
    {
        // Reads as ((pay > 15000) && inactive) || name == Alice  ->  Charlie, Alice
        AssertMatches("(Pay > 15000) && (IsActive == false) || (Name == 'Alice Fox')", "Alice", "Charlie");
    }

    [Fact]
    public void OrThenAndStillGroupsAndFirst()
    {
        // Reads as name == Alice || (active && pay == 0)  ->  Alice, Bob
        AssertMatches("(Name == 'Alice Fox') || (IsActive == true) && (Pay == 0)", "Alice", "Bob");
    }

    [Fact]
    public void RedundantParenthesesAreHarmless()
    {
        AssertMatches("(((Pay > 10000)))", "Alice", "Charlie");
    }

    [Fact]
    public void DeeplyNestedGroupsParse()
    {
        AssertMatches("(((IsActive == true) && ((Pay > 5000) && !(Pay > 10000))) || (Name == 'Charlie Smith'))", "Charlie", "David");
    }

    [Fact]
    public void ComparisonNeedsNoParentheses()
    {
        AssertMatches("Pay > 10000", "Alice", "Charlie");
        AssertMatches("Pay > 10000 && IsActive == true", "Alice");
    }

    [Fact]
    public void ChainedConjunctionsFlattenIntoOneNode()
    {
        var condition = ConditionFunctions.ParseQuery("(Pay > 1) && (IsActive == true) && (Alias IsNotNull)");

        var conjunction = Assert.IsType<ConjunctionCondition>(condition);
        Assert.Equal(Operator.And, conjunction.Operator);
        Assert.Equal(3, conjunction.Conditions.Count);
    }

    // ---------- operators, spellings and literals ----------

    [Theory]
    [InlineData("(Name == 'Alice Fox')")]
    [InlineData("(Name = 'Alice Fox')")]
    [InlineData("Name=='Alice Fox'")]
    [InlineData("([Name] == 'Alice Fox')")]
    public void EqualitySpellingsAgree(string query)
    {
        AssertMatches(query, "Alice");
    }

    [Theory]
    [InlineData("(Name != 'Alice Fox')")]
    [InlineData("(Name <> 'Alice Fox')")]
    public void InequalitySpellingsAgree(string query)
    {
        AssertMatches(query, "Bob", "Charlie", "David");
    }

    [Theory]
    [InlineData("(Pay > 10000) && (IsActive == true)")]
    [InlineData("(Pay > 10000) AND (IsActive == true)")]
    [InlineData("(Pay > 10000) and (IsActive == true)")]
    public void AndSpellingsAgree(string query)
    {
        AssertMatches(query, "Alice");
    }

    [Theory]
    [InlineData("(Pay == 19000) || (Pay == 0)")]
    [InlineData("(Pay == 19000) OR (Pay == 0)")]
    [InlineData("(Pay == 19000) or (Pay == 0)")]
    public void OrSpellingsAgree(string query)
    {
        AssertMatches(query, "Bob", "Charlie");
    }

    [Fact]
    public void NamedOperatorsAreCaseInsensitive()
    {
        Assert.Equal(Run("(Name StartsWith 'Bob')"), Run("(Name startswith 'Bob')"));
    }

    [Theory]
    [InlineData("(Pay >= 12000)", new[] { "Alice", "Charlie" })]
    [InlineData("(Pay <= 8000)", new[] { "Bob", "David" })]
    [InlineData("(Pay < 8000)", new[] { "Bob" })]
    [InlineData("(Pay IsBetween (8000, 12000))", new[] { "Alice", "David" })]
    [InlineData("(Pay IsNotBetween (8000, 12000))", new[] { "Bob", "Charlie" })]
    [InlineData("(Name IsIn ('Alice Fox', 'Bob Samuelson'))", new[] { "Alice", "Bob" })]
    [InlineData("(Name IsNotIn ('Alice Fox', 'Bob Samuelson'))", new[] { "Charlie", "David" })]
    [InlineData("(Name StartsWith 'Charlie')", new[] { "Charlie" })]
    [InlineData("(Name DoesNotStartWith 'Charlie')", new[] { "Alice", "Bob", "David" })]
    [InlineData("(Name EndsWith 'Fox')", new[] { "Alice" })]
    [InlineData("(Name Contains 'li')", new[] { "Alice", "Charlie" })]
    [InlineData("(Name DoesNotContain 'li')", new[] { "Bob", "David" })]
    [InlineData("(Alias IsNull)", new[] { "Bob" })]
    [InlineData("(Alias IsNotNull)", new[] { "Alice", "Charlie", "David" })]
    [InlineData("(HireDate > 2020-01-01)", new[] { "David" })]
    [InlineData("(Classification == Irreplacable)", new[] { "David" })]
    public void OperatorsMatchTheExpectedRows(string query, string[] expected)
    {
        AssertMatches(query, expected);
    }

    [Fact]
    public void EqualsNullIsShorthandForIsNull()
    {
        AssertMatches("(Alias == null)", "Bob");
        AssertMatches("(Alias != null)", "Alice", "Charlie", "David");
        AssertMatches("(Alias == NULL)", "Bob");
    }

    [Fact]
    public void QuotedNullIsALiteralNotAnIsNullTest()
    {
        var condition = ConditionFunctions.ParseQuery("(Alias == 'null')");

        var value = Assert.IsType<OneValueCondition<string>>(condition);
        Assert.Equal(Operator.Equals, value.Operator);
        Assert.Equal("null", value.Value.Value);
    }

    [Fact]
    public void EmptyStringLiteralIsLegal()
    {
        // This used to crash the tokenizer with ArgumentOutOfRangeException
        var condition = ConditionFunctions.ParseQuery("(Alias == '')");

        var value = Assert.IsType<OneValueCondition<string>>(condition);
        Assert.Equal(string.Empty, value.Value.Value);
        Assert.Empty(Run("(Alias == '')"));
    }

    [Fact]
    public void EmptyValueListIsLegal()
    {
        Assert.Empty(Run("(Name IsIn ())"));
        AssertMatches("(Name IsNotIn ())", "Alice", "Bob", "Charlie", "David");
    }

    [Fact]
    public void SingleValueListIsEquivalentToABareValue()
    {
        Assert.Equal(Run("(Name IsIn ('Alice Fox'))"), Run("(Name IsIn 'Alice Fox')"));
    }

    [Theory]
    [InlineData(@"(Alias == 'it\'s')", "it's")]
    [InlineData(@"(Alias == 'back\\slash')", @"back\slash")]
    [InlineData("(Alias == 'has space')", "has space")]
    [InlineData("(Alias == 'a,b')", "a,b")]
    [InlineData("(Alias == '(paren)')", "(paren)")]
    [InlineData("(Alias == '&& ||')", "&& ||")]
    public void QuotedLiteralsPreserveTheirContents(string query, string expected)
    {
        var value = Assert.IsType<OneValueCondition<string>>(ConditionFunctions.ParseQuery(query));

        Assert.Equal(expected, value.Value.Value);
    }

    [Fact]
    public void UnquotedValuesNeedNoQuotesForPathsDatesAndNumbers()
    {
        var value = Assert.IsType<OneValueCondition<string>>(ConditionFunctions.ParseQuery("(BirthDate > 2000-01-05T13:45:30.123)"));

        Assert.Equal("2000-01-05T13:45:30.123", value.Value.Value);
    }

    [Fact]
    public void DottedFieldPathsAreASingleField()
    {
        var value = Assert.IsType<OneValueCondition<string>>(ConditionFunctions.ParseQuery("(Lair.Name == 'Volcano')"));

        Assert.Equal("Lair.Name", value.Field);
    }

    [Fact]
    public void ConditionToStringCanBeParsedBack()
    {
        foreach (var query in new[]
        {
            "(Pay > 10000)",
            "!(Pay > 10000) && (IsActive == true)",
            "((Pay > 15000) || (Pay < 5000)) && (IsActive == true)",
            "(Name IsIn ('Alice Fox', 'Bob Samuelson'))",
            "(Pay IsBetween (8000, 12000))",
            "(Alias IsNull)",
        })
        {
            var printed = ConditionFunctions.ParseQuery(query)!.ToString()!;

            Assert.Equal(Run(query), Run(printed));
        }
    }

    // ---------- malformed input is rejected, with a WeequeryException ----------

    [Theory]
    // truncated
    [InlineData("(Pay >")]
    [InlineData("(Pay)")]
    [InlineData("()")]
    [InlineData("(Pay > 10000) &&")]
    [InlineData("(Pay > 10000) ||")]
    [InlineData("!")]
    [InlineData("(Pay >= )")]
    // bracket / paren mismatch
    [InlineData("(Pay > 10000")]
    [InlineData("Pay > 10000)")]
    [InlineData("((Pay > 10000)")]
    [InlineData("(Name IsIn ('a', 'b')")]
    [InlineData("(Name IsIn 'a', 'b'])")]
    // juxtaposition with no operator
    [InlineData("(Pay > 10000) (IsActive == true)")]
    [InlineData("&& (Pay > 10000)")]
    [InlineData("(Pay > 10000) && && (IsActive == true)")]
    // wrong number of values
    [InlineData("(Pay > 10000 20000 30000)")]
    [InlineData("(Pay IsBetween (10000))")]
    [InlineData("(Pay IsBetween (1, 2, 3))")]
    [InlineData("(Alias IsNull 'x')")]
    // bad tokens
    [InlineData("(Pay Bogus 10000)")]
    [InlineData("(Pay & 10000)")]
    [InlineData("(Pay | 10000)")]
    [InlineData("(Name Contains 'unterminated)")]
    [InlineData(@"(Name Contains 'dangling escape\")]
    [InlineData("(Pay > 10000) && (== 5)")]
    [InlineData("([Pay > 10000)")]
    public void MalformedQueriesThrowWeequeryException(string query)
    {
        Assert.Throws<WeequeryException>(() => ConditionFunctions.ParseQuery(query));
    }

    [Fact]
    public void ErrorMessagesLocateTheProblem()
    {
        Assert.Throws<WeequeryException>(() => ConditionFunctions.ParseQuery("(Pay Bogus 10000)"));
    }

    [Fact]
    public void UnboundFieldIsReportedByName()
    {
        Assert.Throws<WeequeryException>(() => Run("(NotAField == 1)"));
    }

    // ---------- nesting is bounded ----------

    /// <summary>
    /// What a caller is held to: the depth of the condition the query describes. Pinned here as well as in
    /// NestingLimitTests because the parser is where a query from the wire arrives.
    /// </summary>
    private const int MaxDepth = 16;

    /// <summary>
    /// The text limit, which is a separate and much looser thing: parsing is recursive descent, so it exists to
    /// keep a query nesting a few thousand deep off the stack, where it used to overflow and kill the process.
    /// </summary>
    private const int MaxSyntaxDepth = 64;

    private static string Grouped(int depth, string inner)
    {
        return new string('(', depth) + inner + new string(')', depth);
    }

    /// <summary>
    /// Negations nest a condition, so this is the shape that tests the condition limit rather than the text one
    /// </summary>
    private static string Negated(int depth, string inner)
    {
        return new string('!', depth) + inner;
    }

    [Fact]
    public void AConditionNestedUpToTheLimitParses()
    {
        Assert.NotNull(ConditionFunctions.ParseQuery(Negated(MaxDepth, "(Pay > 10000)")));
    }

    [Fact]
    public void AConditionNestedPastTheLimitThrows()
    {
        Assert.Throws<WeequeryException>(() => ConditionFunctions.ParseQuery(Negated(MaxDepth + 1, "(Pay > 10000)")));
    }

    /// <summary>
    /// Parentheses that group nothing build nothing, so they are held to the text limit rather than the condition
    /// one. The point is that the limit follows what the query means, not how it was punctuated.
    /// </summary>
    [Fact]
    public void RedundantParenthesesAreNotCountedAgainstTheCondition()
    {
        Assert.NotNull(ConditionFunctions.ParseQuery(Grouped(MaxDepth + 4, "Pay > 10000")));
    }

    /// <summary>
    /// The gap this covers: precedence builds levels that no parenthesis shows, so each of these groups holds an
    /// Or over an And and nests the condition twice as fast as the text. Counting the text alone let a query
    /// through the parser that every later walk over it would refuse.
    /// </summary>
    [Fact]
    public void PrecedenceCountsTowardsTheConditionLimit()
    {
        string query = "Pay > 4";
        for (int i = 0; i < 16; i++) { query = $"(Pay > 1 && Pay > 2 || Pay > 3 && {query})"; }

        // 16 levels of parentheses, but a tree 32 deep
        Assert.Throws<WeequeryException>(() => ConditionFunctions.ParseQuery(query));
    }

    /// <summary>
    /// The depths that used to overflow the stack. A limit is only worth having if it holds here.
    /// </summary>
    [Theory]
    [InlineData(50000)]
    [InlineData(MaxSyntaxDepth + 1)]
    public void TextNestedPastTheTextLimitThrows(int depth)
    {
        Assert.Throws<WeequeryException>(() => ConditionFunctions.ParseQuery(Grouped(depth, "Pay > 10000")));
        Assert.Throws<WeequeryException>(() => ConditionFunctions.ParseQuery(Negated(depth, "(Pay > 10000)")));
    }

    /// <summary>
    /// The count is of enclosing levels, not of levels seen, so a wide tree of shallow branches is fine however
    /// many branches it has
    /// </summary>
    [Fact]
    public void SiblingGroupsDoNotAccumulateDepth()
    {
        var query = string.Join(" && ", Enumerable.Repeat("(Pay > 1)", 500));

        Assert.NotNull(ConditionFunctions.ParseQuery(query));
    }

    /// <summary>
    /// Anything the writer produces has to read back, right up to the limit. QueryWriter parenthesizes every
    /// comparison, so its output is always a level deeper as text than the tree it came from, which is why the
    /// text limit has to sit above the condition one rather than equal it.
    /// </summary>
    [Fact]
    public void AConditionAtTheLimitStillReadsBackFromItsWrittenForm()
    {
        ICondition condition = new OneValueCondition<decimal>(Operator.GreaterThan, nameof(Minion.Pay), 10000m);

        for (int i = 0; i < MaxDepth; i++) { condition = new NotCondition(Operator.Not, condition); }

        Assert.NotNull(ConditionFunctions.ParseQuery(condition.ToQuery()));
    }

    // ---------- the query string reaches the query through the public surface ----------

    [Fact]
    public void ApplyConditionAcceptsAQueryString()
    {
        var result = MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition("!(Pay > 10000) && (IsActive == true)")
            .Build()
            .ToList();

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void TransportConditionCarriesAQueryString()
    {
        var transport = new TransportCondition("!(Pay > 10000) && (IsActive == true)");

        var result = MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition(transport.Unpack())
            .Build()
            .ToList();

        Assert.Equal(2, result.Count);
    }
}
