using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// One string carrying both a condition and a sort clause, split on the separator that introduces the sorts.
/// </summary>
/// <remarks>
/// The split is found by reading the condition and seeing where it stops rather than by searching the text, which
/// is what keeps a value that spells the separator a value.
/// </remarks>
public class ParsedQueryTests
{
    private static string Describe(IEnumerable<Sort> sorts)
    {
        return string.Join(", ", from sort in sorts select $"{sort.Field} {sort.Direction}");
    }

    private static readonly Sort[] ById = [new("MinionID", SortDirection.Ascending)];

    // ---------- both halves ----------

    [Theory]
    [InlineData("Pay > 10000 ORDER BY Pay DESC", "([Pay] > '10000')", "Pay Descending")]
    [InlineData("Pay > 10000 OrderBy Pay DESC", "([Pay] > '10000')", "Pay Descending")]
    [InlineData("Pay > 10000 order by Pay desc, Name", "([Pay] > '10000')", "Pay Descending, Name Ascending")]
    [InlineData("(Pay > 1) && (IsActive == true) ORDER BY Name", "(([Pay] > '1') && ([IsActive] == 'true'))", "Name Ascending")]
    [InlineData("Alias IsNull ORDER BY Pay DESC", "([Alias] IsNull)", "Pay Descending")]
    [InlineData("Name IsIn ('a', 'b') OrderBy Name", "([Name] IsIn ('a', 'b'))", "Name Ascending")]
    public void ItSplitsAConditionFromItsSorts(string query, string condition, string sorts)
    {
        var parsed = ParsedQuery.Parse(query);

        Assert.NotNull(parsed.Condition);
        Assert.Equal(condition, parsed.Condition.ToQuery());
        Assert.Equal(sorts, Describe(parsed.Sorts));
    }

    /// <summary>
    /// It deconstructs, so the two come apart where they are used
    /// </summary>
    [Fact]
    public void ItDeconstructs()
    {
        var (condition, sorts) = ParsedQuery.Parse("Pay > 10000 ORDER BY Pay DESC");

        Assert.NotNull(condition);
        Assert.Single(sorts);
    }

    // ---------- one half or neither ----------

    /// <summary>
    /// Without the separator the whole string is a condition, and the sorts are whatever the caller settled on
    /// </summary>
    [Fact]
    public void NoSeparatorMakesTheWholeStringACondition()
    {
        var parsed = ParsedQuery.Parse("Pay > 10000");

        Assert.Equal("([Pay] > '10000')", parsed.Condition!.ToQuery());
        Assert.Empty(parsed.Sorts);
    }

    [Theory]
    [InlineData("ORDER BY Pay DESC")]
    [InlineData("OrderBy Pay DESC")]
    [InlineData("order by Pay desc")]
    public void ASeparatorAtTheFrontMeansSortsAndNoFiltering(string query)
    {
        var parsed = ParsedQuery.Parse(query);

        Assert.Null(parsed.Condition);
        Assert.Equal("Pay Descending", Describe(parsed.Sorts));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingAtAllIsNoConditionAndTheDefault(string? query)
    {
        var parsed = ParsedQuery.Parse(query, ById);

        Assert.Null(parsed.Condition);
        Assert.Equal("MinionID Ascending", Describe(parsed.Sorts));
    }

    /// <summary>
    /// The default stands in wherever the string named no sorts, and is replaced wherever it named some
    /// </summary>
    [Theory]
    [InlineData("Pay > 10000", "MinionID Ascending")]
    [InlineData("", "MinionID Ascending")]
    [InlineData("Pay > 10000 ORDER BY Pay DESC", "Pay Descending")]
    [InlineData("ORDER BY Pay DESC", "Pay Descending")]
    public void TheDefaultStandsInOnlyWhereNoSortsWereNamed(string query, string expected)
    {
        Assert.Equal(expected, Describe(ParsedQuery.Parse(query, ById).Sorts));
    }

    // ---------- the split is not a text search ----------

    /// <summary>
    /// The thing that makes this worth doing properly: a value can spell anything, so the separator is found by
    /// reading the condition rather than by looking for the words in the text
    /// </summary>
    [Theory]
    [InlineData("Name == 'ORDER BY'")]
    [InlineData("Name == 'OrderBy'")]
    [InlineData("Name Contains 'order by pay desc'")]
    [InlineData("Name IsIn ('ORDER BY', 'OrderBy')")]
    public void AValueThatSpellsTheSeparatorIsStillAValue(string query)
    {
        var parsed = ParsedQuery.Parse(query);

        Assert.NotNull(parsed.Condition);
        Assert.Empty(parsed.Sorts);

        // And the condition is the whole of what was written, values included
        Assert.Equal(ConditionFunctions.ParseQuery(query)!.ToQuery(), parsed.Condition.ToQuery());
    }

    /// <summary>
    /// Even with a real sort clause after it
    /// </summary>
    [Fact]
    public void AValueSpellingTheSeparatorDoesNotHideARealOne()
    {
        var parsed = ParsedQuery.Parse("Name == 'ORDER BY' ORDER BY Pay DESC");

        Assert.Equal("([Name] == 'ORDER BY')", parsed.Condition!.ToQuery());
        Assert.Equal("Pay Descending", Describe(parsed.Sorts));
    }

    /// <summary>
    /// A field named Order is fine, since only ORDER followed by BY is the separator
    /// </summary>
    [Fact]
    public void AFieldNamedOrderIsStillAField()
    {
        var parsed = ParsedQuery.Parse("Order > 5 ORDER BY Order DESC");

        Assert.Equal("([Order] > '5')", parsed.Condition!.ToQuery());
        Assert.Equal("Order Descending", Describe(parsed.Sorts));
    }

    /// <summary>
    /// A field named OrderBy at the very front is read as the separator, exactly as it is in a sort clause on its
    /// own, and takes the same escape
    /// </summary>
    [Fact]
    public void AFieldNamedOrderByAtTheFrontNeedsItsBrackets()
    {
        Assert.Throws<WeequeryException>(() => ParsedQuery.Parse("OrderBy > 5"));

        var parsed = ParsedQuery.Parse("[OrderBy] > 5 ORDER BY Pay");

        Assert.Equal("([OrderBy] > '5')", parsed.Condition!.ToQuery());
        Assert.Equal("Pay Ascending", Describe(parsed.Sorts));
    }

    // ---------- writing both halves back out ----------

    [Theory]
    [InlineData("Pay > 10000 ORDER BY Pay DESC", "([Pay] > '10000') ORDER BY [Pay] DESC")]
    [InlineData("Pay > 10000 OrderBy Pay DESC, Name", "([Pay] > '10000') ORDER BY [Pay] DESC, [Name] ASC")]
    [InlineData("Pay > 10000", "([Pay] > '10000')")]
    [InlineData("ORDER BY Pay DESC", "ORDER BY [Pay] DESC")]
    public void ItWritesBothHalvesAsOneString(string query, string expected)
    {
        Assert.Equal(expected, ParsedQuery.Parse(query).ToQuery());
    }

    /// <summary>
    /// The separator is written whatever the style, because here it is the only thing telling the parser where
    /// the condition stopped. The style reaches the condition, which does have operators spelled two ways.
    /// </summary>
    [Fact]
    public void TheSeparatorIsWrittenWhateverTheStyle()
    {
        var parsed = ParsedQuery.Parse("(Pay > 1) && (IsActive == true) ORDER BY Pay DESC");

        Assert.Equal("(([Pay] > '1') && ([IsActive] == 'true')) ORDER BY [Pay] DESC", parsed.ToQuery(QueryStyle.CSharp));
        Assert.Equal("(([Pay] > '1') And ([IsActive] = 'true')) ORDER BY [Pay] DESC", parsed.ToQuery(QueryStyle.Sql));
    }

    /// <summary>
    /// The property it exists for, and the one a caller rolling this by hand gets wrong: the sort half must
    /// carry the separator or the halves cannot be told apart again
    /// </summary>
    [Theory]
    [InlineData("Pay > 10000 ORDER BY Pay DESC, Name")]
    [InlineData("Pay > 10000 OrderBy Name")]
    [InlineData("Pay > 10000")]
    [InlineData("ORDER BY Pay DESC")]
    [InlineData("Name == 'ORDER BY' ORDER BY Pay")]
    [InlineData("(Pay > 1) || (Alias IsNull) ORDER BY 'Hire Date' DESC")]
    public void WhatIsWrittenReadsBack(string query)
    {
        var parsed = ParsedQuery.Parse(query);

        var written = parsed.ToQuery();
        var again = ParsedQuery.Parse(written);

        Assert.Equal(written, again.ToQuery());
        Assert.Equal(parsed.Sorts, again.Sorts);
        Assert.Equal(parsed.Condition?.ToQuery(), again.Condition?.ToQuery());
    }

    [Theory]
    [InlineData(QueryStyle.CSharp)]
    [InlineData(QueryStyle.Sql)]
    public void BothStylesReadBack(QueryStyle style)
    {
        var parsed = ParsedQuery.Parse("(Pay > 1) && (IsActive == true) ORDER BY Pay DESC, Name");

        Assert.Equal(parsed.Sorts, ParsedQuery.Parse(parsed.ToQuery(style)).Sorts);
    }

    /// <summary>
    /// Neither half missing leaves anything dangling, a bare separator least of all
    /// </summary>
    [Fact]
    public void NothingToWriteIsTheEmptyString()
    {
        Assert.Equal(string.Empty, new ParsedQuery(null, []).ToQuery());
        Assert.Equal(string.Empty, new ParsedQuery(null, []).ToQuery(QueryStyle.Sql));

        // And reads back as nothing at all
        var (condition, sorts) = ParsedQuery.Parse(new ParsedQuery(null, []).ToQuery());

        Assert.Null(condition);
        Assert.Empty(sorts);
    }

    [Fact]
    public void ItPrintsAsItsOwnText()
    {
        Assert.Equal("([Pay] > '10000') ORDER BY [Pay] DESC", ParsedQuery.Parse("Pay > 10000 ORDER BY Pay DESC").ToString());
    }

    // ---------- malformed ----------

    [Theory]
    [InlineData("Pay > 10000 Name < 5")]                    // two conditions with nothing between them
    [InlineData("Pay > ORDER BY Pay")]                       // a comparison with no value
    [InlineData("Pay > 10000 ORDER BY")]                     // a separator and nothing to sort by
    [InlineData("Pay > 10000 ORDER BY Pay Name")]            // a malformed sort clause
    [InlineData("Pay Bogus 10000 ORDER BY Pay")]             // a malformed condition
    [InlineData("(Pay > 10000 ORDER BY Pay")]                // an unclosed group
    [InlineData("Pay > 10000 ORDER BY Pay DESC,")]           // a trailing separator in the sorts
    public void AMalformedQueryIsRefused(string query)
    {
        Assert.Throws<WeequeryException>(() => ParsedQuery.Parse(query));
    }

    /// <summary>
    /// A refusal points at the text, the same as either parser on its own does
    /// </summary>
    [Fact]
    public void ARefusalQuotesTheQuery()
    {
        var ex = Assert.Throws<WeequeryException>(() => ParsedQuery.Parse("Pay > 10000 Name < 5"));

        Assert.Contains("Name", ex.Message);
    }

    // ---------- and it filters and orders ----------

    /// <summary>
    /// What the whole thing is for: one string in, the right rows in the right order out
    /// </summary>
    [Theory]
    [InlineData("Pay > 5000 ORDER BY Pay DESC", new[] { "Charlie", "Alice", "David" })]
    [InlineData("Pay > 5000 OrderBy Name DESC", new[] { "David", "Charlie", "Alice" })]
    [InlineData("IsActive == true ORDER BY Pay", new[] { "Bob", "David", "Alice" })]
    [InlineData("ORDER BY Pay DESC", new[] { "Charlie", "Alice", "David", "Bob" })]
    public void AParsedQueryFiltersAndOrders(string query, string[] expected)
    {
        var (condition, sorts) = ParsedQuery.Parse(query);

        var rows = MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition(condition)
            .ApplySorts(sorts)
            .Build()
            .Select(minion => minion.Name.Split(' ')[0])
            .ToArray();

        Assert.Equal(expected, rows);
    }
}
