using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// Reading a sort clause from text. Separate text from a condition, so the two travel apart and neither has to
/// know about the other, see <see cref="Sort.Parse"/>.
/// </summary>
public class SortParserTests
{
    private static string Describe(IEnumerable<Sort> sorts)
    {
        return string.Join(", ", from sort in sorts select $"{sort.Field} {sort.Direction}");
    }

    private static readonly Sort[] ByName = [new("Name", SortDirection.Ascending)];

    // ---------- the shape of a clause ----------

    [Theory]
    [InlineData("Pay", "Pay Ascending")]
    [InlineData("Pay ASC", "Pay Ascending")]
    [InlineData("Pay Ascending", "Pay Ascending")]
    [InlineData("Pay DESC", "Pay Descending")]
    [InlineData("Pay Descending", "Pay Descending")]
    [InlineData("Pay DESC, Name", "Pay Descending, Name Ascending")]
    [InlineData("Pay DESC, Name ASC, Alias DESC", "Pay Descending, Name Ascending, Alias Descending")]
    public void ItReadsAFieldAndItsDirection(string clause, string expected)
    {
        Assert.Equal(expected, Describe(Sort.Parse(clause, null)));
    }

    /// <summary>
    /// A field on its own runs ascending, which is what SQL assumes and what a caller writing only a name means
    /// </summary>
    [Fact]
    public void AFieldWithNoDirectionIsAscending()
    {
        Assert.Equal(SortDirection.Ascending, Assert.Single(Sort.Parse("Pay", null)).Direction);
    }

    /// <summary>
    /// The prefix is optional, spelled either as two words or as one, and means the same thing every way
    /// </summary>
    [Theory]
    [InlineData("ORDER BY Pay DESC, Name")]
    [InlineData("order by Pay DESC, Name")]
    [InlineData("Order By Pay DESC, Name")]
    [InlineData("OrderBy Pay DESC, Name")]
    [InlineData("orderby Pay DESC, Name")]
    [InlineData("ORDERBY Pay DESC, Name")]
    public void TheOrderByPrefixIsOptionalAndCaseInsensitive(string clause)
    {
        Assert.Equal(Describe(Sort.Parse("Pay DESC, Name", null)), Describe(Sort.Parse(clause, null)));
    }

    /// <summary>
    /// Everything in the clause is matched without regard to case, like the rest of the language
    /// </summary>
    [Theory]
    [InlineData("Pay desc")]
    [InlineData("Pay DESC")]
    [InlineData("Pay DeScEnDiNg")]
    public void ADirectionIsCaseInsensitive(string clause)
    {
        Assert.Equal(SortDirection.Descending, Assert.Single(Sort.Parse(clause, null)).Direction);
    }

    /// <summary>
    /// A field is written the way a condition writes one, so a key needing quotes reads the same in both.
    /// Brackets hold a single word, since whitespace ends one; a quoted field can hold anything.
    /// </summary>
    [Theory]
    [InlineData("[HireDate] DESC", "HireDate Descending")]
    [InlineData("[Pay], [Name] DESC", "Pay Ascending, Name Descending")]
    [InlineData("'Hire Date' DESC", "Hire Date Descending")]
    [InlineData("\"Hire Date\"", "Hire Date Ascending")]
    [InlineData("Lair.Name", "Lair.Name Ascending")]
    public void AFieldIsWrittenTheWayAConditionWritesOne(string clause, string expected)
    {
        Assert.Equal(expected, Describe(Sort.Parse(clause, null)));
    }

    /// <summary>
    /// Which means brackets around a name with a space in it are refused, exactly as they are in a condition. No
    /// binding can hold such a key anyway, since a key may not contain whitespace.
    /// </summary>
    [Fact]
    public void BracketsDoNotHoldAFieldWithASpaceInIt()
    {
        Assert.Throws<WeequeryException>(() => Sort.Parse("[Hire Date] DESC", null));
    }

    // ---------- nothing to read ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void NothingToReadTakesTheDefault(string? clause)
    {
        Assert.Equal("Name Ascending", Describe(Sort.Parse(clause, ByName)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NothingToReadAndNoDefaultIsAnEmptyList(string? clause)
    {
        var sorts = Sort.Parse(clause, null);

        Assert.NotNull(sorts);
        Assert.Empty(sorts);
    }

    /// <summary>
    /// A clause that reads takes precedence over the default rather than adding to it
    /// </summary>
    [Fact]
    public void AClauseReplacesTheDefaultRatherThanExtendingIt()
    {
        Assert.Equal("Pay Descending", Describe(Sort.Parse("Pay DESC", ByName)));
    }

    /// <summary>
    /// The default is copied, so what comes back is the caller's to change
    /// </summary>
    [Fact]
    public void TheDefaultIsCopiedRatherThanHandedOut()
    {
        List<Sort> defaults = [new("Name", SortDirection.Ascending)];

        var sorts = Sort.Parse(null, defaults);
        sorts.Add(new Sort("Pay", SortDirection.Descending));

        Assert.Single(defaults);
        Assert.Equal(2, sorts.Count);
    }

    /// <summary>
    /// The default is optional, since most callers have none
    /// </summary>
    [Fact]
    public void TheDefaultCanBeLeftOutEntirely()
    {
        Assert.Equal("Pay Descending", Describe(Sort.Parse("Pay DESC")));
        Assert.Empty(Sort.Parse(null));
    }

    // ---------- words that are only words where they can be ----------

    /// <summary>
    /// ORDER and BY are read only as a pair and only at the front, so a field really named Order still sorts.
    /// Nothing had to be reserved for the prefix.
    /// </summary>
    [Theory]
    [InlineData("Order", "Order Ascending")]
    [InlineData("Order DESC", "Order Descending")]
    [InlineData("Order, Pay", "Order Ascending, Pay Ascending")]
    [InlineData("ORDER BY Order DESC", "Order Descending")]
    [InlineData("By", "By Ascending")]
    public void AFieldNamedForThePrefixStillReads(string clause, string expected)
    {
        Assert.Equal(expected, Describe(Sort.Parse(clause, null)));
    }

    /// <summary>
    /// The one word spelling has nothing after it to tell the two apart, so a leading bare OrderBy is always the
    /// prefix. That is the one place the escape is needed, and it is the same escape any other collision takes.
    /// </summary>
    [Theory]
    [InlineData("[OrderBy]", "OrderBy Ascending")]
    [InlineData("[OrderBy] DESC", "OrderBy Descending")]
    [InlineData("'OrderBy' DESC", "OrderBy Descending")]
    [InlineData("Pay, OrderBy", "Pay Ascending, OrderBy Ascending")]
    [InlineData("OrderBy [OrderBy] DESC", "OrderBy Descending")]
    public void AFieldNamedOrderByIsWrittenInBrackets(string clause, string expected)
    {
        Assert.Equal(expected, Describe(Sort.Parse(clause, null)));
    }

    /// <summary>
    /// And bare, at the front, it is the prefix rather than the field, so on its own there is nothing left to
    /// sort by
    /// </summary>
    [Fact]
    public void ABareOrderByAtTheFrontIsThePrefix()
    {
        Assert.Throws<WeequeryException>(() => Sort.Parse("OrderBy", null));

        // Which is the same answer the two word spelling gives
        Assert.Throws<WeequeryException>(() => Sort.Parse("ORDER BY", null));
    }

    /// <summary>
    /// Which is why no binding may be named OrderBy: rather than leave a key that can be bound but not sorted on
    /// bare, the name is refused where the binding is made. ORDER and BY need no such treatment, being read only
    /// together, so a field named Order binds and sorts like any other.
    /// </summary>
    [Fact]
    public void NoBindingMayBeNamedOrderBy()
    {
        Assert.Throws<WeequeryException>(() => MinionTestData.Minions().WithWeequery().BindProperty(minion => minion.Name, "OrderBy"));
        Assert.Throws<WeequeryException>(() => MinionTestData.Minions().WithWeequery().BindProperty(minion => minion.Name, "orderby"));

        // And so every key that can be bound can be sorted on
        var ordered = MinionTestData.Minions()
            .WithWeequery()
            .BindProperty(minion => minion.Name, "Order")
            .ApplySorts("Order DESC")
            .Build()
            .Select(minion => minion.Name.Split(' ')[0])
            .ToArray();

        Assert.Equal(["David", "Charlie", "Bob", "Alice"], ordered);
    }

    /// <summary>
    /// And a direction is read only after a field has been, so a field named Desc reads as the field
    /// </summary>
    [Theory]
    [InlineData("Desc", "Desc Ascending")]
    [InlineData("Desc DESC", "Desc Descending")]
    [InlineData("Asc, Desc", "Asc Ascending, Desc Ascending")]
    public void AFieldNamedForADirectionStillReads(string clause, string expected)
    {
        Assert.Equal(expected, Describe(Sort.Parse(clause, null)));
    }

    // ---------- malformed ----------

    [Theory]
    [InlineData("Pay Name")]            // two fields with no comma
    [InlineData("Pay DESC Name")]       // same, after a direction
    [InlineData("Pay,")]                // trailing separator
    [InlineData(",")]                   // nothing but a separator
    [InlineData(", Pay")]               // leading separator
    [InlineData("Pay,, Name")]          // a gap in the list
    [InlineData("ORDER BY")]            // a prefix and nothing to sort by
    [InlineData("Pay DESC,")]
    [InlineData("[Pay")]                // an unclosed bracket
    [InlineData("[] DESC")]             // an empty bracket
    [InlineData("'unterminated")]
    [InlineData("Pay ASC DESC")]        // two directions
    public void AMalformedClauseIsRefused(string clause)
    {
        Assert.Throws<WeequeryException>(() => Sort.Parse(clause, ByName));
    }

    /// <summary>
    /// A refusal points at the text rather than merely saying no, the same as a condition's does
    /// </summary>
    [Fact]
    public void ARefusalQuotesTheClause()
    {
        var ex = Assert.Throws<WeequeryException>(() => Sort.Parse("Pay Name", null));

        Assert.Contains("Pay Name", ex.Message);
    }

    // ---------- and it sorts ----------

    /// <summary>
    /// What the whole thing is for: text in, rows in that order out
    /// </summary>
    [Theory]
    [InlineData("Pay DESC", new[] { "Charlie", "Alice", "David", "Bob" })]
    [InlineData("Pay", new[] { "Bob", "David", "Alice", "Charlie" })]
    [InlineData("ORDER BY IsActive, Pay DESC", new[] { "Charlie", "Alice", "David", "Bob" })]
    public void ParsedSortsOrderTheRows(string clause, string[] expected)
    {
        var ordered = MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplySorts(Sort.Parse(clause, null))
            .Build()
            .Select(minion => minion.Name.Split(' ')[0])
            .ToArray();

        Assert.Equal(expected, ordered);
    }

    // ---------- writing one back out ----------

    /// <summary>
    /// One canonical spelling, the way writing a condition settles on one per operator: the field bracketed, the
    /// direction always stated and always short
    /// </summary>
    [Theory]
    [InlineData("Pay", "[Pay] ASC")]
    [InlineData("Pay DESC", "[Pay] DESC")]
    [InlineData("Pay Descending, Name", "[Pay] DESC, [Name] ASC")]
    [InlineData("ORDER BY Pay descending, name ascending", "[Pay] DESC, [name] ASC")]
    [InlineData("'Hire Date' DESC", "'Hire Date' DESC")]
    [InlineData("Lair.Name", "[Lair.Name] ASC")]
    public void ItWritesOneCanonicalForm(string clause, string expected)
    {
        Assert.Equal(expected, Sort.Parse(clause, null).ToQuery());
    }

    /// <summary>
    /// The SQL style writes the prefix the parser accepts but does not need
    /// </summary>
    [Fact]
    public void TheSqlStyleWritesTheOrderBy()
    {
        Assert.Equal("ORDER BY [Pay] DESC, [Name] ASC", Sort.Parse("Pay DESC, Name", null).ToQuery(QueryStyle.Sql));
        Assert.Equal("[Pay] DESC, [Name] ASC", Sort.Parse("Pay DESC, Name", null).ToQuery(QueryStyle.CSharp));
    }

    /// <summary>
    /// The property the writer exists for: what is written reads back as the same sorts, and writing those again
    /// gives the same text. Unlike a condition this is exact rather than only equivalent, since a sort carries no
    /// value to be read against a property's type on the way through.
    /// </summary>
    [Theory]
    [InlineData("Pay")]
    [InlineData("Pay DESC")]
    [InlineData("Pay DESC, Name")]
    [InlineData("ORDER BY Pay Descending, Name Ascending, Alias DESC")]
    [InlineData("OrderBy Pay DESC")]
    [InlineData("[Lair.Name] DESC, [Pay]")]
    [InlineData("'Hire Date' DESC")]
    [InlineData("Order DESC")]
    [InlineData("[OrderBy] DESC")]
    [InlineData("Desc DESC")]
    public void WhatIsWrittenReadsBackAsTheSameSorts(string clause)
    {
        var sorts = Sort.Parse(clause, null);

        var written = sorts.ToQuery();
        var again = Sort.Parse(written, null);

        Assert.Equal(sorts, again);
        Assert.Equal(written, again.ToQuery());
    }

    /// <summary>
    /// And both styles read back, so writing one is never a one way trip
    /// </summary>
    [Theory]
    [InlineData(QueryStyle.CSharp)]
    [InlineData(QueryStyle.Sql)]
    public void BothStylesReadBack(QueryStyle style)
    {
        var sorts = Sort.Parse("Pay DESC, Name, Alias Descending", null);

        Assert.Equal(sorts, Sort.Parse(sorts.ToQuery(style), null));
    }

    /// <summary>
    /// Nothing to write is the empty string rather than a bare prefix, which would not read back
    /// </summary>
    [Fact]
    public void NothingToWriteIsTheEmptyString()
    {
        Assert.Equal(string.Empty, new List<Sort>().ToQuery());
        Assert.Equal(string.Empty, new List<Sort>().ToQuery(QueryStyle.Sql));
        Assert.Equal(string.Empty, ((IEnumerable<Sort>?)null).ToQuery());

        // And reads back as nothing at all
        Assert.Empty(Sort.Parse(new List<Sort>().ToQuery(), null));
    }

    /// <summary>
    /// A sort prints as the text it would be written as, the way a condition does
    /// </summary>
    [Fact]
    public void ASortPrintsAsItsOwnText()
    {
        Assert.Equal("[Pay] DESC", new Sort("Pay", SortDirection.Descending).ToString());
        Assert.Equal("[Name] ASC", new Sort("Name", SortDirection.Ascending).ToString());
        Assert.Equal("'Hire Date' ASC", new Sort("Hire Date", SortDirection.Ascending).ToString());
    }

    [Fact]
    public void WritingRefusesASortThatNamesNoField()
    {
        Assert.Throws<WeequeryException>(() => new Sort("", SortDirection.Ascending).ToQuery());
        Assert.Throws<WeequeryException>(() => new List<Sort> { new("Pay", SortDirection.Ascending), new("", SortDirection.Ascending) }.ToQuery());
    }

    // ---------- straight onto a query ----------

    /// <summary>
    /// ApplySorts takes the clause directly, so a caller with one string needs no parse of its own
    /// </summary>
    [Fact]
    public void ApplySortsTakesTheClauseItself()
    {
        var ordered = MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplySorts("Pay DESC")
            .Build()
            .Select(minion => minion.Name.Split(' ')[0])
            .ToArray();

        Assert.Equal(["Charlie", "Alice", "David", "Bob"], ordered);
    }

    /// <summary>
    /// And takes the default with it, which is the shape a paged query wants: whatever the caller asked for, or
    /// something that breaks every tie if they asked for nothing
    /// </summary>
    [Theory]
    [InlineData("Pay DESC", new[] { "Charlie", "Alice", "David", "Bob" })]
    [InlineData(null, new[] { "Alice", "Bob", "Charlie", "David" })]
    [InlineData("", new[] { "Alice", "Bob", "Charlie", "David" })]
    public void ApplySortsFallsBackToTheDefault(string? clause, string[] expected)
    {
        var ordered = MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplySorts(clause, [new Sort(nameof(Minion.Name), SortDirection.Ascending)])
            .Build()
            .Select(minion => minion.Name.Split(' ')[0])
            .ToArray();

        Assert.Equal(expected, ordered);
    }

    /// <summary>
    /// The string overload sits beside the one taking a list without disturbing it. A bare null still reaches the
    /// list overload and sorts by nothing, because an overload needing no default argument is preferred to one
    /// that does; worth pinning, since the alternative is a call that quietly changed meaning.
    /// </summary>
    [Fact]
    public void TheTwoOverloadsDoNotCollide()
    {
        static string[] Ordered(Func<Inquiry<Minion>, Inquiry<Minion>> apply)
        {
            return [.. apply(MinionTestData.Minions().WithWeequery().BindProperties(Minion.Bindings))
                .Build()
                .Select(minion => minion.Name.Split(' ')[0])];
        }

        // Unsorted, so the order the set was written in
        Assert.Equal(["Alice", "Bob", "Charlie", "David"], Ordered(query => query.ApplySorts(null)));

        Assert.Equal(["Charlie", "Alice", "David", "Bob"], Ordered(query => query.ApplySorts("Pay DESC")));
        Assert.Equal(["Charlie", "Alice", "David", "Bob"], Ordered(query => query.ApplySorts([new Sort(nameof(Minion.Pay), SortDirection.Descending)])));
    }

    /// <summary>
    /// A malformed clause is refused where it is read, before anything is applied
    /// </summary>
    [Fact]
    public void ApplySortsRefusesAMalformedClause()
    {
        Assert.Throws<WeequeryException>(() => MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplySorts("Pay Name"));
    }

    /// <summary>
    /// A field no binding claimed is refused when the sort is applied, not when it is read: the parser knows the
    /// text and the query knows the bindings, the same division a condition follows
    /// </summary>
    [Fact]
    public void AnUnboundFieldIsRefusedWhenApplied()
    {
        var sorts = Sort.Parse("NotAField DESC", null);

        Assert.Single(sorts);

        Assert.Throws<WeequeryException>(() => MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplySorts(sorts)
            .Build()
            .ToList());
    }
}
