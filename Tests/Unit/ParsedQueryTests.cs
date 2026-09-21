// The C# and SQL styles are deprecated, and these tests are part of why the deprecation is safe: they pin
// what those styles still write and still read. Deprecated is not gone.
#pragma warning disable CS0618

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
    [InlineData("Pay > 10000 OrderBy Pay DESC", "([Pay] > '10000')", "Pay Descending")]
    [InlineData("Pay > 10000 orderby Pay desc, Name", "([Pay] > '10000')", "Pay Descending, Name Ascending")]
    [InlineData("(Pay > 1) AND (IsActive == true) OrderBy Name", "(([Pay] > '1') AND ([IsActive] = 'true'))", "Name Ascending")]
    [InlineData("Alias IsNull OrderBy Pay DESC", "([Alias] IsNull)", "Pay Descending")]
    [InlineData("Name IsIn ('a', 'b') OrderBy Name", "([Name] IsIn ('a', 'b'))", "Name Ascending")]
    public void ItSplitsAConditionFromItsSorts(string query, string condition, string sorts)
    {
        var parsed = ParsedQuery.Parse(query);

        Assert.NotNull(parsed.Condition);
        Assert.Equal(condition, parsed.Condition.ToQuery());
        Assert.Equal(sorts, Describe(parsed.Sorts));
    }

    /// <summary>
    /// The same string in the spellings the deprecated styles read: the split lands in the same place and the
    /// condition parses to the same tree, so what those styles produced still splits. Deprecated is not gone,
    /// see <see cref="NativeStyleTests.ACombinedStringIsRefusedForItsSeparatorToo"/>.
    /// </summary>
    [Theory]
    [InlineData("Pay > 10000 ORDER BY Pay DESC", "([Pay] > '10000')", "Pay Descending")]
    [InlineData("Pay > 10000 order by Pay desc, Name", "([Pay] > '10000')", "Pay Descending, Name Ascending")]
    [InlineData("(Pay > 1) && (IsActive == true) ORDER BY Name", "(([Pay] > '1') AND ([IsActive] = 'true'))", "Name Ascending")]
    [InlineData("Alias IS NULL ORDER BY Pay DESC", "([Alias] IsNull)", "Pay Descending")]
    public void TheDeprecatedSpellingsSplitTheSameWay(string query, string condition, string sorts)
    {
        var parsed = ParsedQuery.Parse(query, null, QueryStyle.Sql);

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
        var parsed = ParsedQuery.Parse("Pay > 10000 OrderBy Pay DESC");

        Assert.NotNull(parsed.Condition);
        Assert.Single(parsed.Sorts);
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
    [InlineData("ORDERBY Pay DESC")]
    [InlineData("OrderBy Pay DESC")]
    [InlineData("orderby Pay desc")]
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
    [InlineData("Pay > 10000 OrderBy Pay DESC", "Pay Descending")]
    [InlineData("OrderBy Pay DESC", "Pay Descending")]
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
        var parsed = ParsedQuery.Parse("Name == 'ORDER BY' OrderBy Pay DESC");

        Assert.Equal("([Name] = 'ORDER BY')", parsed.Condition!.ToQuery());
        Assert.Equal("Pay Descending", Describe(parsed.Sorts));
    }

    /// <summary>
    /// A field named Order is fine, since only ORDER followed by BY is the separator
    /// </summary>
    [Fact]
    public void AFieldNamedOrderIsStillAField()
    {
        var parsed = ParsedQuery.Parse("Order > 5 OrderBy Order DESC");

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

        var parsed = ParsedQuery.Parse("[OrderBy] > 5 OrderBy Pay");

        Assert.Equal("([OrderBy] > '5')", parsed.Condition!.ToQuery());
        Assert.Equal("Pay Ascending", Describe(parsed.Sorts));
    }

    // ---------- writing both halves back out ----------

    [Theory]
    [InlineData("Pay > 10000 OrderBy Pay DESC", "([Pay] > '10000') OrderBy [Pay] DESC")]
    [InlineData("Pay > 10000 OrderBy Pay DESC, Name", "([Pay] > '10000') OrderBy [Pay] DESC, [Name] ASC")]
    [InlineData("Pay > 10000", "([Pay] > '10000')")]
    [InlineData("OrderBy Pay DESC", "OrderBy [Pay] DESC")]
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
        var parsed = ParsedQuery.Parse("(Pay > 1) AND (IsActive == true) OrderBy Pay DESC");

        Assert.Equal("(([Pay] > '1') && ([IsActive] == 'true')) ORDER BY [Pay] DESC", parsed.ToQuery(QueryStyle.CSharp));
        Assert.Equal("(([Pay] > '1') And ([IsActive] = 'true')) ORDER BY [Pay] DESC", parsed.ToQuery(QueryStyle.Sql));
    }

    /// <summary>
    /// The property it exists for, and the one a caller rolling this by hand gets wrong: the sort half must
    /// carry the separator or the halves cannot be told apart again
    /// </summary>
    [Theory]
    [InlineData("Pay > 10000 OrderBy Pay DESC, Name")]
    [InlineData("Pay > 10000 OrderBy Name")]
    [InlineData("Pay > 10000")]
    [InlineData("OrderBy Pay DESC")]
    [InlineData("Name == 'ORDER BY' OrderBy Pay")]
    [InlineData("(Pay > 1) OR (Alias IsNull) OrderBy 'Hire Date' DESC")]
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
        var parsed = ParsedQuery.Parse("(Pay > 1) AND (IsActive == true) OrderBy Pay DESC, Name");

        Assert.Equal(parsed.Sorts, ParsedQuery.Parse(parsed.ToQuery(style), null, style).Sorts);
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
        var parsed = ParsedQuery.Parse(new ParsedQuery(null, []).ToQuery());

        Assert.Null(parsed.Condition);
        Assert.Empty(parsed.Sorts);
    }

    [Fact]
    public void ItPrintsAsItsOwnText()
    {
        Assert.Equal("([Pay] > '10000') OrderBy [Pay] DESC", ParsedQuery.Parse("Pay > 10000 OrderBy Pay DESC").ToString());
    }

    // ---------- malformed ----------

    [Theory]
    [InlineData("Pay > 10000 Name < 5")]                    // two conditions with nothing between them
    [InlineData("Pay > OrderBy Pay")]                       // a comparison with no value
    [InlineData("Pay > 10000 OrderBy")]                     // a separator and nothing to sort by
    [InlineData("Pay > 10000 OrderBy Pay Name")]            // a malformed sort clause
    [InlineData("Pay Bogus 10000 OrderBy Pay")]             // a malformed condition
    [InlineData("(Pay > 10000 OrderBy Pay")]                // an unclosed group
    [InlineData("Pay > 10000 OrderBy Pay DESC,")]           // a trailing separator in the sorts
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

    // ---------- and the projection after them ----------

    private static string Fields(ParsedQuery parsed)
    {
        return string.Join(", ", parsed.Projection.Fields);
    }

    [Theory]
    [InlineData("Pay > 10000 OrderBy Pay DESC Select Name, Pay", "([Pay] > '10000')", "Pay Descending", "Name, Pay")]
    [InlineData("Pay > 10000 OrderBy Pay DESC Select Name", "([Pay] > '10000')", "Pay Descending", "Name")]
    [InlineData("Pay > 10000 Select Name, Pay", "([Pay] > '10000')", "", "Name, Pay")]
    [InlineData("OrderBy Pay DESC Select Name", null, "Pay Descending", "Name")]
    [InlineData("Select Name, Pay", null, "", "Name, Pay")]
    [InlineData("Pay > 1 Select [Name], 'Hire Date'", "([Pay] > '1')", "", "Name, Hire Date")]
    [InlineData("Pay > 1 Select Tallies[apples]", "([Pay] > '1')", "", "Tallies[apples]")]
    [InlineData("Pay > 1 select name", "([Pay] > '1')", "", "name")]
    public void AProjectionFollowsWhateverCameBeforeIt(string query, string? condition, string sorts, string fields)
    {
        var parsed = ParsedQuery.Parse(query);

        Assert.Equal(condition, parsed.Condition?.ToQuery());
        Assert.Equal(sorts, Describe(parsed.Sorts));
        Assert.Equal(fields, Fields(parsed));
    }

    /// <summary>
    /// The part is optional, so everything written before it existed still means what it meant
    /// </summary>
    [Theory]
    [InlineData("Pay > 10000 OrderBy Pay DESC")]
    [InlineData("Pay > 10000")]
    [InlineData("OrderBy Pay DESC")]
    [InlineData("")]
    public void NoSelectMeansTheWholeRow(string query)
    {
        Assert.True(ParsedQuery.Parse(query).Projection.IsEmpty);
    }

    [Fact]
    public void ItDeconstructsIntoThree()
    {
        var parsed = ParsedQuery.Parse("Pay > 10000 OrderBy Pay DESC Select Name");

        Assert.NotNull(parsed.Condition);
        Assert.Single(parsed.Sorts);
        Assert.Equal(["Name"], parsed.Projection.Fields);
    }

    /// <summary>
    /// And into two, for the callers who were deconstructing it before there was a third
    /// </summary>
    [Fact]
    public void ItStillDeconstructsIntoTwo()
    {
        var parsed = ParsedQuery.Parse("Pay > 10000 OrderBy Pay DESC Select Name");

        Assert.NotNull(parsed.Condition);
        Assert.Single(parsed.Sorts);
    }

    /// <summary>
    /// A projection says nothing about sorting, so the default stands in exactly as it does without one
    /// </summary>
    [Fact]
    public void TheDefaultSortStandsInBesideAProjection()
    {
        Assert.Equal("MinionID Ascending", Describe(ParsedQuery.Parse("Pay > 1 Select Name", ById).Sorts));
        Assert.Equal("MinionID Ascending", Describe(ParsedQuery.Parse("Select Name", ById).Sorts));

        // and does not where the string named sorts of its own
        Assert.Equal("Pay Descending", Describe(ParsedQuery.Parse("Pay > 1 OrderBy Pay DESC Select Name", ById).Sorts));
    }

    /// <summary>
    /// Found by reading, not by searching, which is the same rule the sort separator follows
    /// </summary>
    [Theory]
    [InlineData("Name = 'Select'")]
    [InlineData("Name = 'Select' OrderBy Pay")]
    [InlineData("Name IsIn ('Select', 'OrderBy')")]
    public void AValueThatSpellsSelectIsStillAValue(string query)
    {
        var parsed = ParsedQuery.Parse(query);

        Assert.NotNull(parsed.Condition);
        Assert.True(parsed.Projection.IsEmpty);
    }

    /// <summary>
    /// A field named Select where a part could begin is read as the separator, and takes the same escape OrderBy
    /// takes. No binding may be named for it either, see ReservedKeyTests.
    /// </summary>
    [Fact]
    public void AFieldNamedSelectAtTheFrontNeedsItsBrackets()
    {
        Assert.Throws<WeequeryException>(() => ParsedQuery.Parse("Select > 5"));

        var parsed = ParsedQuery.Parse("[Select] > 5 Select Name");

        Assert.Equal("([Select] > '5')", parsed.Condition!.ToQuery());
        Assert.Equal("Name", Fields(parsed));
    }

    /// <summary>
    /// The order is fixed, because where a part sits is the only thing saying which part it is
    /// </summary>
    [Theory]
    [InlineData("Pay > 1 Select Name OrderBy Pay")]          // the wrong way round
    [InlineData("Pay > 1 Select")]                            // a separator and nothing to read
    [InlineData("Select")]                                    // the same, with nothing before it
    [InlineData("Pay > 1 OrderBy Pay Select")]                // and the same again after sorts
    [InlineData("Pay > 1 Select Name Select Pay")]            // twice
    [InlineData("Pay > 1 Select Name Pay")]                   // a malformed field list
    [InlineData("Pay > 1 Select Name,")]                      // a trailing separator in the fields
    public void AMisplacedOrEmptyProjectionIsRefused(string query)
    {
        Assert.Throws<WeequeryException>(() => ParsedQuery.Parse(query));
    }

    /// <summary>
    /// A Select naming nothing is refused rather than read as the projection that names nothing, which is what
    /// leaving the word off already says
    /// </summary>
    [Fact]
    public void ASelectNamingNothingSaysSo()
    {
        var error = Assert.Throws<WeequeryException>(() => ParsedQuery.Parse("Pay > 1 Select"));

        Assert.Equal(WeequeryError.QuerySyntax, error.Error);
        Assert.Contains("Select", error.Message, StringComparison.Ordinal);
    }

    // ---------- writing the projection back out ----------

    [Theory]
    [InlineData("Pay > 10000 OrderBy Pay DESC Select Name, Pay", "([Pay] > '10000') OrderBy [Pay] DESC Select [Name], [Pay]")]
    [InlineData("Pay > 10000 Select Name", "([Pay] > '10000') Select [Name]")]
    [InlineData("OrderBy Pay Select Name", "OrderBy [Pay] ASC Select [Name]")]
    [InlineData("Select Name", "Select [Name]")]
    public void ItWritesTheProjectionAsPartOfTheString(string query, string expected)
    {
        Assert.Equal(expected, ParsedQuery.Parse(query).ToQuery());
    }

    /// <summary>
    /// The style reaches the condition and the sort separator, and finds nothing to decide in a projection: a
    /// field list has no operators and Select is one word everywhere
    /// </summary>
    [Fact]
    public void TheProjectionIsWrittenTheSameWhateverTheStyle()
    {
        var parsed = ParsedQuery.Parse("(Pay > 1) AND (IsActive == true) OrderBy Pay DESC Select Name");

        Assert.Equal("(([Pay] > '1') && ([IsActive] == 'true')) ORDER BY [Pay] DESC Select [Name]", parsed.ToQuery(QueryStyle.CSharp));
        Assert.Equal("(([Pay] > '1') And ([IsActive] = 'true')) ORDER BY [Pay] DESC Select [Name]", parsed.ToQuery(QueryStyle.Sql));
    }

    [Theory]
    [InlineData("Pay > 10000 OrderBy Pay DESC Select Name, Pay")]
    [InlineData("Pay > 10000 Select Name")]
    [InlineData("OrderBy Pay DESC Select 'Hire Date'")]
    [InlineData("Select Tallies[apples], Name")]
    [InlineData("Name = 'Select' OrderBy Pay Select Name")]
    public void WhatIsWrittenWithAProjectionReadsBack(string query)
    {
        var parsed = ParsedQuery.Parse(query);

        var written = parsed.ToQuery();
        var again = ParsedQuery.Parse(written);

        Assert.Equal(written, again.ToQuery());
        Assert.Equal(parsed.Projection.Fields, again.Projection.Fields);
        Assert.Equal(parsed.Sorts, again.Sorts);
        Assert.Equal(parsed.Condition?.ToQuery(), again.Condition?.ToQuery());
    }

    /// <summary>
    /// Null is the nothing a caller meant by it, on the constructor as on a with
    /// </summary>
    [Fact]
    public void NoProjectionGivenIsNoProjection()
    {
        Assert.True(new ParsedQuery(null, []).Projection.IsEmpty);
        Assert.True(new ParsedQuery(null, [], null).Projection.IsEmpty);

        var cleared = ParsedQuery.Parse("Select Name") with { Projection = null };

        Assert.NotNull(cleared.Projection);
        Assert.True(cleared.Projection.IsEmpty);

        Assert.Equal(string.Empty, new ParsedQuery(null, [], null).ToQuery());
    }

    // ---------- and it filters, orders and projects ----------

    /// <summary>
    /// One string in, the right rows in the right order holding the right columns out
    /// </summary>
    [Fact]
    public void AParsedQueryFiltersOrdersAndProjects()
    {
        var parsed = ParsedQuery.Parse("Pay > 5000 OrderBy Pay DESC Select Name, Pay");

        var rows = MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition(parsed.Condition)
            .ApplySorts(parsed.Sorts)
            .ApplyProjection(parsed.Projection)
            .BuildProjected()
            .ToList();

        Assert.Equal(["Charlie Smith", "Alice Fox", "David Edgars"], rows.Select(row => (string)row["Name"]!));
        Assert.Equal(["Name", "Pay"], rows[0].Keys);
    }

    // ---------- and it filters and orders ----------

    /// <summary>
    /// What the whole thing is for: one string in, the right rows in the right order out
    /// </summary>
    [Theory]
    [InlineData("Pay > 5000 OrderBy Pay DESC", new[] { "Charlie", "Alice", "David" })]
    [InlineData("Pay > 5000 OrderBy Name DESC", new[] { "David", "Charlie", "Alice" })]
    [InlineData("IsActive == true OrderBy Pay", new[] { "Bob", "David", "Alice" })]
    [InlineData("OrderBy Pay DESC", new[] { "Charlie", "Alice", "David", "Bob" })]
    public void AParsedQueryFiltersAndOrders(string query, string[] expected)
    {
        var parsed = ParsedQuery.Parse(query);

        var rows = MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition(parsed.Condition)
            .ApplySorts(parsed.Sorts)
            .Build()
            .Select(minion => minion.Name.Split(' ')[0])
            .ToArray();

        Assert.Equal(expected, rows);
    }
}
