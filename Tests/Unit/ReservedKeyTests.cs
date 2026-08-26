using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// A binding key is written as a bare field name, so a key that spells an operator makes a query that reads two
/// ways. The parser could settle most of them by position, a field coming before an operator, but not the
/// conjunctions: the tokenizer reads AND, OR and NOT as themselves wherever they appear, so a field named "And"
/// cannot be written at all. All of them are refused rather than only the ones that break.
/// </summary>
public class ReservedKeyTests
{
    private class Thing
    {
        public string Name { get; set; } = "";

        /// <summary>A property named for an operator, which is the case that has to be given a key</summary>
        public bool Contains { get; set; }
    }

    private static IQueryable<Thing> Things()
    {
        return new List<Thing> { new() { Name = "a", Contains = true }, new() { Name = "b", Contains = false } }.AsQueryable();
    }

    /// <summary>
    /// Stated here rather than derived, so the test says what the contract is instead of repeating how it is
    /// worked out
    /// </summary>
    public static TheoryData<string> ReservedWords()
    {
        return new TheoryData<string>(
            // the conjunctions and the null literal, which the tokenizer claims wherever they appear
            "AND", "OR", "NOT", "NULL",
            // the words SQL spells its operators with
            "IS", "IN", "BETWEEN",
            // and every operator's own name
            "IsNull", "IsNotNull", "IsIn", "IsNotIn", "IsBetween", "IsNotBetween",
            "StartsWith", "DoesNotStartWith", "EndsWith", "DoesNotEndWith", "Contains", "DoesNotContain");
    }

    [Theory]
    [MemberData(nameof(ReservedWords))]
    public void AKeyThatSpellsAnOperatorIsRefused(string key)
    {
        Assert.Throws<WeequeryException>(() => Things().WithWeequery().BindProperty(thing => thing.Name, key));
    }

    /// <summary>
    /// Nothing about a key is case sensitive, the refusal included
    /// </summary>
    [Theory]
    [InlineData("contains")]
    [InlineData("CONTAINS")]
    [InlineData("cOnTaInS")]
    [InlineData("and")]
    [InlineData("isin")]
    public void TheRefusalDoesNotDependOnCase(string key)
    {
        Assert.Throws<WeequeryException>(() => Things().WithWeequery().BindProperty(thing => thing.Name, key));
    }

    /// <summary>
    /// Every route a key arrives by is held to it
    /// </summary>
    [Fact]
    public void EveryRouteRefusesIt()
    {
        Assert.Throws<WeequeryException>(() => new BindingRequest(nameof(Thing.Name), "In"));
        Assert.Throws<WeequeryException>(() => new BindingRequest([nameof(Thing.Name)], "In"));
        Assert.Throws<WeequeryException>(() => Things().WithWeequery().BindProperty(nameof(Thing.Name), "In"));
        Assert.Throws<WeequeryException>(() => Things().WithWeequery().BindProperty(thing => thing.Name, "In"));
        Assert.Throws<WeequeryException>(() => Things().WithWeequery().BindProperty(thing => thing.Name, ["Length"], "In"));
        Assert.Throws<WeequeryException>(() => Things().WithWeequery().BindProperties([new BindingRequest(nameof(Thing.Name), null) { Key = "In" }]));
    }

    /// <summary>
    /// A property named for an operator takes its key from its own name, so binding it without one is refused at
    /// the point the binding is made rather than when a query using it will not parse
    /// </summary>
    [Fact]
    public void APropertyNamedForAnOperatorMustBeGivenAKey()
    {
        Assert.Throws<WeequeryException>(() => Things().WithWeequery().BindProperty(thing => thing.Contains));
    }

    [Fact]
    public void APropertyNamedForAnOperatorWorksUnderAnotherKey()
    {
        var matched = Things()
            .WithWeequery()
            .BindProperty(thing => thing.Contains, "ContainsFlag")
            .ApplyCondition("ContainsFlag == true")
            .Build()
            .Count();

        Assert.Equal(1, matched);
    }

    /// <summary>
    /// Only the words themselves are claimed, not everything that starts like one
    /// </summary>
    [Theory]
    [InlineData("Container")]
    [InlineData("Andes")]
    [InlineData("Inbound")]
    [InlineData("Notes")]
    [InlineData("IsNullable")]
    [InlineData("Nullity")]
    [InlineData("Betweenness")]
    [InlineData("Ins")]
    public void AKeyThatMerelyStartsLikeOneIsFine(string key)
    {
        var matched = Things()
            .WithWeequery()
            .BindProperty(thing => thing.Name, key)
            .ApplyCondition($"{key} == 'a'")
            .Build()
            .Count();

        Assert.Equal(1, matched);
    }

    /// <summary>
    /// The reserved set is read from the Operator enum rather than listed, so an operator added later is reserved
    /// by having been added. This checks that has not come loose.
    /// </summary>
    [Fact]
    public void EveryOperatorNameIsReserved()
    {
        foreach (var op in Enum.GetValues<Operator>())
        {
            var spelling = ConditionFunctions.GetOperationString(op);

            // The symbolic ones could never be a key anyway, since a key has to be a name
            if (!WeequeryException.IsSqlName(spelling)) { continue; }

            Assert.Throws<WeequeryException>(() => Things().WithWeequery().BindProperty(thing => thing.Name, spelling));
        }
    }

    /// <summary>
    /// A value is not a key: text that spells an operator is quoted and read as itself, which is how a caller
    /// filters for the word "Between"
    /// </summary>
    [Fact]
    public void AValueMaySpellAnOperator()
    {
        var rows = new List<Thing> { new() { Name = "Between" }, new() { Name = "b" } }.AsQueryable();

        Assert.Equal(1, rows.WithWeequery().BindProperty(thing => thing.Name).ApplyCondition("Name == 'Between'").Build().Count());
        Assert.Equal(1, rows.WithWeequery().BindProperty(thing => thing.Name).ApplyCondition("Name IsIn ('Between', 'AND')").Build().Count());
    }

    /// <summary>
    /// And the bindings that were already in use are untouched
    /// </summary>
    [Fact]
    public void TheExistingSharedBindingsStillBind()
    {
        Assert.Equal(4, MinionTestData.Minions().WithWeequery().BindProperties(Minion.Bindings).Build().Count());
    }
}
