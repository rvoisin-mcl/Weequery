using Tests.Common;
using Weequery;
using Weequery.Interfaces;

namespace Tests.Unit;

/// <summary>
/// Every walk over a condition is recursive, and a condition usually arrives from a caller, so each walk caps how
/// deep it will go. Left uncapped, a tree nesting a few thousand levels overflowed the stack, which cannot be
/// caught and killed the process.
/// <para>
/// The query parser's own limit is covered in QueryParserTests. These cover the other two walks, unpacking a
/// PackedCondition and building the expression, which take a tree rather than text.
/// </para>
/// </summary>
public class NestingLimitTests
{
    private const int MaxDepth = 16;

    /// <summary>
    /// The depth that used to overflow the stack. A limit is only worth having if it holds here, and the tree is
    /// built iteratively so that constructing it does not overflow first.
    /// </summary>
    private const int OverflowDepth = 50000;

    /// <summary>
    /// A tree of nested negations wrapped around one comparison, so the depth is exactly the count asked for
    /// </summary>
    private static ICondition NestedCondition(int depth)
    {
        ICondition condition = new OneValueCondition<decimal>(Operator.GreaterThan, nameof(Minion.Pay), 10000m);

        for (int i = 0; i < depth; i++) { condition = new NotCondition(Operator.Not, condition); }

        return condition;
    }

    /// <summary>
    /// The same shape packed, built directly rather than by Pack() so that packing a deep tree, which is a
    /// recursive walk of its own, is not what fails
    /// </summary>
    private static PackedCondition NestedPacked(int depth)
    {
        var packed = new PackedCondition(Operator.GreaterThan, nameof(Minion.Pay), [ConditionValue.Raw("10000")], []);

        for (int i = 0; i < depth; i++) { packed = new PackedCondition(Operator.Not, "", [], [packed]); }

        return packed;
    }

    // ---------- Unpack ----------

    [Fact]
    public void UnpackingUpToTheLimitWorks()
    {
        Assert.NotNull(NestedPacked(MaxDepth).Unpack());
    }

    [Fact]
    public void UnpackingPastTheLimitThrows()
    {
        Assert.Throws<WeequeryException>(() => NestedPacked(MaxDepth + 1).Unpack());
    }

    [Fact]
    public void UnpackingDeepEnoughToOverflowTheStackThrows()
    {
        Assert.Throws<WeequeryException>(() => NestedPacked(OverflowDepth).Unpack());
    }

    /// <summary>
    /// The limit is on nesting, not on size: a conjunction with any number of operands is one level
    /// </summary>
    [Fact]
    public void ManySiblingsUnpackFine()
    {
        var operands = Enumerable.Range(0, 500).Select(_ => new PackedCondition(Operator.GreaterThan, nameof(Minion.Pay), [ConditionValue.Raw("1")], [])).ToList();

        Assert.NotNull(new PackedCondition(Operator.And, "", [], operands).Unpack());
    }

    // ---------- BuildExpression ----------

    [Fact]
    public void BuildingUpToTheLimitWorks()
    {
        Assert.NotNull(Inquiry<Minion>.BuildExpression(Minion.Bindings, NestedCondition(MaxDepth)));
    }

    [Fact]
    public void BuildingPastTheLimitThrows()
    {
        Assert.Throws<WeequeryException>(() => Inquiry<Minion>.BuildExpression(Minion.Bindings, NestedCondition(MaxDepth + 1)));
    }

    [Fact]
    public void BuildingDeepEnoughToOverflowTheStackThrows()
    {
        Assert.Throws<WeequeryException>(() => Inquiry<Minion>.BuildExpression(Minion.Bindings, NestedCondition(OverflowDepth)));
    }

    [Fact]
    public void ManySiblingsBuildFine()
    {
        List<ICondition> operands = Enumerable.Range(0, 500).Select(ICondition (_) => new OneValueCondition<decimal>(Operator.GreaterThan, nameof(Minion.Pay), 1m)).ToList();

        Assert.NotNull(Inquiry<Minion>.BuildExpression(Minion.Bindings, new ConjunctionCondition(Operator.And, operands)));
    }

    // ---------- Pack ----------

    [Fact]
    public void PackingUpToTheLimitWorks()
    {
        Assert.NotNull(NestedCondition(MaxDepth).Pack());
    }

    [Fact]
    public void PackingPastTheLimitThrows()
    {
        Assert.Throws<WeequeryException>(() => NestedCondition(MaxDepth + 1).Pack());
    }

    [Fact]
    public void PackingDeepEnoughToOverflowTheStackThrows()
    {
        Assert.Throws<WeequeryException>(() => NestedCondition(OverflowDepth).Pack());
    }

    /// <summary>
    /// The depth is held for the thread rather than passed down, so it has to unwind on the way out. If it did
    /// not, a refused pack would leave the count high and poison every pack after it on that thread.
    /// </summary>
    [Fact]
    public void ARefusedPackDoesNotPoisonTheNextOne()
    {
        Assert.Throws<WeequeryException>(() => NestedCondition(MaxDepth + 1).Pack());

        Assert.NotNull(NestedCondition(MaxDepth).Pack());
    }

    // ---------- ToQuery and ToString ----------

    [Fact]
    public void WritingUpToTheLimitWorks()
    {
        Assert.NotEmpty(NestedCondition(MaxDepth).ToQuery());
    }

    [Fact]
    public void WritingPastTheLimitThrows()
    {
        Assert.Throws<WeequeryException>(() => NestedCondition(MaxDepth + 1).ToQuery());
    }

    [Fact]
    public void WritingDeepEnoughToOverflowTheStackThrows()
    {
        Assert.Throws<WeequeryException>(() => NestedCondition(OverflowDepth).ToQuery());
    }

    /// <summary>
    /// ToString has to stay usable on anything, since throwing from it makes debugging worse, so it says where it
    /// stopped rather than refusing. Same reasoning as the other shapes the language cannot express.
    /// </summary>
    [Fact]
    public void ToStringOnADeepConditionSaysWhereItStopped()
    {
        var described = NestedCondition(OverflowDepth).ToString();

        Assert.NotNull(described);
        Assert.Contains("too deep", described);
    }

    /// <summary>
    /// A packed condition renders by unpacking, and unpacking a deep tree throws, so ToString has to absorb that
    /// too rather than passing it on
    /// </summary>
    [Fact]
    public void ToStringOnADeepPackedConditionSaysWhereItStopped()
    {
        var described = NestedPacked(OverflowDepth).ToString();

        Assert.NotNull(described);
        Assert.Contains("too deep", described);
    }

    [Fact]
    public void AConditionWithinTheLimitStillWritesNormally()
    {
        Assert.Equal("!!([Pay] > 10000)", NestedCondition(2).ToQuery());
    }

    // ---------- through the public surface ----------

    [Fact]
    public void ApplyingADeepConditionToAQueryThrows()
    {
        Assert.Throws<WeequeryException>(() => MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition(NestedCondition(OverflowDepth))
            .Build());
    }

    [Fact]
    public void ADeepConditionOffTheWireThrows()
    {
        var transport = new TransportCondition(NestedPacked(OverflowDepth));

        Assert.Throws<WeequeryException>(() => transport.Unpack());
    }

    /// <summary>
    /// A condition of ordinary depth still round trips through pack, unpack and build
    /// </summary>
    [Fact]
    public void AConditionWithinTheLimitStillWorksEndToEnd()
    {
        var packed = NestedPacked(4);

        var result = MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition(packed.Unpack())
            .Build()
            .ToList();

        // Four negations cancel out, so this is just "pay > 10000": Alice and Charlie
        Assert.Equal(2, result.Count);
    }
}
