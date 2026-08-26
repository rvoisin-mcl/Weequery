using Tests.Common;
using Weequery;
using Weequery.Interfaces;

namespace Tests.Unit;

/// <summary>
/// Either quote character marks a literal, and a literal closes on whichever one opened it.
/// <para>
/// Only the single quote used to be a delimiter. A double quoted value was not rejected, it was taken as a bare
/// word with the quotes left in, so <c>Name == "Alice"</c> quietly filtered for the five character text
/// <c>"Alice"</c>. Only the cases that happened to contain a space or an apostrophe failed loudly.
/// </para>
/// </summary>
public class QuoteStyleTests
{
    private static OneValueCondition<string> ParseValue(string query)
    {
        return Assert.IsType<OneValueCondition<string>>(ConditionFunctions.ParseQuery(query));
    }

    /// <summary>
    /// The values a query parses to, whichever of the comparison shapes its operator calls for
    /// </summary>
    private static List<string> ParseValues(string query)
    {
        return Assert.IsAssignableFrom<IBoundCondition>(ConditionFunctions.ParseQuery(query)).StringifyValues();
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

    // ---------- the two are interchangeable ----------

    [Theory]
    [InlineData("Name == 'Alice Fox'", "Name == \"Alice Fox\"")]
    [InlineData("Name StartsWith 'Al'", "Name StartsWith \"Al\"")]
    [InlineData("Name IsIn ('Alice Fox', 'Bob Samuelson')", "Name IsIn (\"Alice Fox\", \"Bob Samuelson\")")]
    [InlineData("Alias == ''", "Alias == \"\"")]
    [InlineData("Name DoesNotContain 'li'", "Name DoesNotContain \"li\"")]
    public void EitherQuoteGivesTheSameCondition(string single, string @double)
    {
        Assert.Equal(ParseValues(single), ParseValues(@double));
        Assert.Equal(Matching(single), Matching(@double));
    }

    [Fact]
    public void ADoubleQuotedValueLosesItsQuotes()
    {
        // The regression this covers: the value used to come through as the text including the quote characters
        Assert.Equal("Alice Fox", ParseValue("Name == \"Alice Fox\"").Value.Value);
        Assert.Equal("Alice", ParseValue("Name == \"Alice\"").Value.Value);
    }

    [Fact]
    public void ADoubleQuotedEmptyValueIsAnEmptyString()
    {
        Assert.Equal(string.Empty, ParseValue("Alias == \"\"").Value.Value);
    }

    [Fact]
    public void ADoubleQuotedValueWithASpaceParses()
    {
        Assert.Equal(["Alice"], Matching("Name == \"Alice Fox\""));
    }

    [Fact]
    public void MixedQuotesInOneQueryAreFine()
    {
        Assert.Equal(["Alice"], Matching("(Name == \"Alice Fox\") && (Alias == 'Ghost')"));
    }

    [Fact]
    public void EitherQuoteWorksForAFieldName()
    {
        Assert.Equal("my field", Assert.IsType<OneValueCondition<string>>(ConditionFunctions.ParseQuery("\"my field\" == 'x'")).Field);
        Assert.Equal("my field", Assert.IsType<OneValueCondition<string>>(ConditionFunctions.ParseQuery("'my field' == 'x'")).Field);
    }

    // ---------- each quote needs no escaping inside the other ----------

    [Fact]
    public void AnApostropheNeedsNoEscapeInsideDoubleQuotes()
    {
        Assert.Equal("it's", ParseValue("Name == \"it's\"").Value.Value);
        Assert.Equal("O'Brien", ParseValue("Name == \"O'Brien\"").Value.Value);
    }

    [Fact]
    public void ADoubleQuoteNeedsNoEscapeInsideSingleQuotes()
    {
        Assert.Equal("say \"hi\"", ParseValue("Name == 'say \"hi\"'").Value.Value);
    }

    [Fact]
    public void BackslashEscapesStillWorkInBothForms()
    {
        Assert.Equal("it's", ParseValue(@"Name == 'it\'s'").Value.Value);
        Assert.Equal("say \"hi\"", ParseValue("Name == \"say \\\"hi\\\"\"").Value.Value);
        Assert.Equal(@"back\slash", ParseValue(@"Name == 'back\\slash'").Value.Value);
        Assert.Equal(@"back\slash", ParseValue("Name == \"back\\\\slash\"").Value.Value);
    }

    /// <summary>
    /// Only a quote and a backslash can be escaped, because they are the only two there is no other way to write.
    /// A backslash in front of anything else is a backslash, and stays in the value with what follows it, so a
    /// value that is a regular expression reads as the one it looks like.
    /// </summary>
    [Theory]
    [InlineData(@"Name == '\w'", @"\w")]
    [InlineData(@"Name == '\d+'", @"\d+")]
    [InlineData(@"Name == 'C:\path\to'", @"C:\path\to")]
    [InlineData(@"Name == '\p{Lu}'", @"\p{Lu}")]
    [InlineData(@"Name == 'ends\'", null)] // the escaped quote leaves the literal unterminated
    public void ABackslashIsOnlyAnEscapeInFrontOfSomethingItCanEscape(string query, string? expected)
    {
        if (expected is null)
        {
            Assert.Throws<WeequeryException>(() => ConditionFunctions.ParseQuery(query));

            return;
        }

        Assert.Equal(expected, ParseValue(query).Value.Value);
    }

    /// <summary>
    /// Which leaves both escapable characters reachable, whichever quote the value is written in
    /// </summary>
    [Fact]
    public void AQuoteAndABackslashAreBothStillReachable()
    {
        Assert.Equal(@"a'b\c", ParseValue(@"Name == 'a\'b\\c'").Value.Value);
        Assert.Equal(@"a""b\c", ParseValue("Name == \"a\\\"b\\\\c\"").Value.Value);

        // And the value survives being written back out, which is what the writer escapes for
        var condition = new OneValueCondition<string>(Operator.Equals, nameof(Minion.Name), @"a'b\c");

        Assert.Equal(@"a'b\c", ParseValue(condition.ToQuery().Trim('(', ')')).Value.Value);
    }

    [Fact]
    public void AQuoteOfOneKindDoesNotCloseTheOther()
    {
        // The closing quote is the one that opened the literal, so an inner quote is just a character
        Assert.Equal("a'b\"c", ParseValue("Name == 'a\\'b\"c'").Value.Value);
        Assert.Equal("a'b\"c", ParseValue("Name == \"a'b\\\"c\"").Value.Value);
    }

    // ---------- unterminated is still an error, and says which quote ----------

    [Fact]
    public void AnUnterminatedDoubleQuoteIsReported()
    {
        Assert.Throws<WeequeryException>(() => ConditionFunctions.ParseQuery("Name == \"unterminated"));
    }

    [Fact]
    public void AnUnterminatedSingleQuoteIsReported()
    {
        Assert.Throws<WeequeryException>(() => ConditionFunctions.ParseQuery("Name == 'unterminated"));
    }

    /// <summary>
    /// The pair has to match. A literal opened with one quote is not closed by the other, so this runs off the end.
    /// </summary>
    [Fact]
    public void MismatchedQuotesDoNotPair()
    {
        Assert.Throws<WeequeryException>(() => ConditionFunctions.ParseQuery("Name == \"mismatched'"));
        Assert.Throws<WeequeryException>(() => ConditionFunctions.ParseQuery("Name == 'mismatched\""));
    }

    // ---------- the writer keeps up with the tokenizer ----------

    /// <summary>
    /// The writer quotes with single quotes, so a value containing a double quote round trips without needing the
    /// double quote escaped. This also shows the tokenizer's bare-word rule was updated: a double quote is now a
    /// terminator, so such a value can never be written bare.
    /// </summary>
    [Fact]
    public void AValueContainingADoubleQuoteRoundTrips()
    {
        var condition = new OneValueCondition<string>(Operator.Equals, nameof(Minion.Name), "say \"hi\"");

        var written = condition.ToQuery();
        Assert.Equal("([Name] == 'say \"hi\"')", written);

        Assert.Equal("say \"hi\"", ParseValue(written).Value.Value);
    }

    [Fact]
    public void AValueContainingBothQuotesRoundTrips()
    {
        var condition = new OneValueCondition<string>(Operator.Equals, nameof(Minion.Name), "it's a \"test\"");

        Assert.Equal("it's a \"test\"", ParseValue(condition.ToQuery()).Value.Value);
    }
}
