using Tests.Common;
using Weequery;
using Weequery.Interfaces;

namespace Tests.Unit;

/// <summary>
/// What a condition does with input that is wrong rather than merely unmatched: a value that is missing, a
/// condition handed over in its packed shape, a query too long to quote back. Each of these used to fail somewhere
/// unhelpful in the framework, at the wrong field, or in a log.
/// </summary>
public class ConditionInputTests
{
    private static IQueryable<Minion> Minions()
    {
        return MinionTestData.Minions();
    }

    private static int Count(ICondition condition)
    {
        return Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition(condition)
            .Build()
            .Count();
    }

    // ---------- a null is not a value ----------

    /// <summary>
    /// It used to build, and then throw ArgumentNullException out of string.StartsWith when the expression ran,
    /// which is a framework exception from well away from the condition that caused it
    /// </summary>
    [Theory]
    [InlineData(Operator.StartsWith)]
    [InlineData(Operator.EndsWith)]
    [InlineData(Operator.Contains)]
    [InlineData(Operator.DoesNotContain)]
    [InlineData(Operator.Equals)]
    [InlineData(Operator.NotEqual)]
    public void ANullValueIsRefusedWhenTheConditionIsBuilt(Operator op)
    {
        Assert.Throws<WeequeryException>(() => new OneValueCondition<string>(op, nameof(Minion.Name), (string)null!));
    }

    [Fact]
    public void ANullAmongOtherValuesIsRefusedAndSaidWhichOne()
    {
        Assert.Throws<WeequeryException>(() => new MultipleValueCondition<string>(Operator.IsIn, nameof(Minion.Alias), ["Ghost", null!, "Snake"]));
    }

    /// <summary>
    /// A nullable value type is the same story: null means no value, and IsNull is how to ask about that
    /// </summary>
    [Fact]
    public void ANullNullableValueIsRefusedToo()
    {
        Assert.Throws<WeequeryException>(() => new OneValueCondition<DateTime?>(Operator.Equals, nameof(Minion.FireDate), (DateTime?)null));
    }

    /// <summary>
    /// The route it actually arrives by: JSON with a null in the values array
    /// </summary>
    [Fact]
    public void ANullValueOffTheWireIsRefusedWhenUnpacked()
    {
        var packed = new PackedCondition(Operator.StartsWith, nameof(Minion.Name), [null!], []);

        Assert.Throws<WeequeryException>(() => packed.Unpack());
    }

    [Fact]
    public void TheValuelessOperatorsAreUnaffected()
    {
        Assert.Equal(3, Count(new NoValueCondition(Operator.IsNull, nameof(Minion.FireDate))));
        Assert.Equal(1, Count(new NoValueCondition(Operator.IsNotNull, nameof(Minion.FireDate))));
    }

    // ---------- a packed condition builds as what it carries ----------

    /// <summary>
    /// A PackedCondition is an ICondition, so a caller passing a request DTO's Condition rather than its Unpack()
    /// compiles. It used to fail with "Unbound field: ''", naming a field nobody wrote.
    /// </summary>
    [Fact]
    public void APackedComparisonBuildsAsTheConditionItCarries()
    {
        ICondition condition = new OneValueCondition<decimal>(Operator.GreaterThan, nameof(Minion.Pay), 10000m);

        Assert.Equal(Count(condition), Count(condition.Pack()));
    }

    [Fact]
    public void APackedConjunctionBuildsAsTheConditionItCarries()
    {
        ICondition condition = new ConjunctionCondition(Operator.And,
        [
            new OneValueCondition<decimal>(Operator.GreaterThan, nameof(Minion.Pay), 1000m),
            new OneValueCondition<bool>(Operator.Equals, nameof(Minion.IsActive), true),
        ]);

        Assert.Equal(2, Count(condition.Pack()));
    }

    [Fact]
    public void APackedNegationBuildsAsTheConditionItCarries()
    {
        ICondition condition = new NotCondition(Operator.Not, new OneValueCondition<decimal>(Operator.GreaterThan, nameof(Minion.Pay), 10000m));

        Assert.Equal(Count(condition), Count(condition.Pack()));
    }

    /// <summary>
    /// And nested inside a tree, since a packed condition can be a child like anything else
    /// </summary>
    [Fact]
    public void APackedConditionNestedInATreeBuildsToo()
    {
        ICondition packedChild = new OneValueCondition<decimal>(Operator.GreaterThan, nameof(Minion.Pay), 10000m).Pack();

        var tree = new ConjunctionCondition(Operator.And, [packedChild, new OneValueCondition<bool>(Operator.Equals, nameof(Minion.IsActive), true)]);

        Assert.Equal(1, Count(tree));
    }

    /// <summary>
    /// The shape a caller most often has one in
    /// </summary>
    [Fact]
    public void ATransportConditionsPackedConditionBuildsWithoutUnpacking()
    {
        var transport = new TransportCondition(new OneValueCondition<decimal>(Operator.GreaterThan, nameof(Minion.Pay), 10000m));

        Assert.NotNull(transport.Condition);
        Assert.Equal(2, Count(transport.Condition));
    }

    /// <summary>
    /// A field the packed condition names is still checked against the bindings, so unpacking it did not loosen
    /// anything
    /// </summary>
    [Fact]
    public void APackedConditionOnAnUnboundFieldIsStillRefused()
    {
        var packed = new PackedCondition(Operator.Equals, "NotAField", [ConditionValue.Raw("1")], []);

        Assert.Throws<WeequeryException>(() => Count(packed));
    }

    // ---------- an error message quotes an excerpt, not the whole query ----------

    /// <summary>
    /// A query is caller input and a malformed one can be any length. Quoting all of it put that length into an
    /// exception message, and from there into a log.
    /// </summary>
    [Fact]
    public void AnErrorOnALongQueryQuotesOnlyAnExcerpt()
    {
        var padding = string.Join(" || ", Enumerable.Repeat("(Pay > 1)", 500));
        var query = $"{padding} || (Pay Bogus 2)";

        var ex = Assert.Throws<WeequeryException>(() => ConditionFunctions.ParseQuery(query));

        Assert.True(ex.Message.Length < 300, $"message was {ex.Message.Length} characters for a {query.Length} character query");

    }

    [Fact]
    public void AnErrorAtTheEndOfALongQueryIsStillShort()
    {
        var query = $"{string.Join(" || ", Enumerable.Repeat("(Pay > 1)", 500))} || (Pay >";

        var ex = Assert.Throws<WeequeryException>(() => ConditionFunctions.ParseQuery(query));

        Assert.True(ex.Message.Length < 300, $"message was {ex.Message.Length} characters");
    }

    /// <summary>
    /// The depth refusal quotes an excerpt as well, since a query nested past the limit is long by construction
    /// </summary>
    [Fact]
    public void TheNestingRefusalQuotesOnlyAnExcerpt()
    {
        string query = "Pay > 4";
        for (int i = 0; i < 16; i++) { query = $"(Pay > 1 && Pay > 2 || Pay > 3 && {query})"; }

        var ex = Assert.Throws<WeequeryException>(() => ConditionFunctions.ParseQuery(query));

        Assert.True(ex.Message.Length < 300, $"message was {ex.Message.Length} characters for a {query.Length} character query");
    }
}
