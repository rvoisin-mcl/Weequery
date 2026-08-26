using Tests.Common;
using Weequery;
using Weequery.Interfaces;

namespace Tests.Unit;

public class BuildTests
{
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

    private static ICondition Active()
    {
        return new OneValueCondition<bool>(Operator.Equals, nameof(Minion.IsActive), true);
    }

    // ---------- conjunctions with no operands ----------

    /// <summary>
    /// These used to throw "Incorrect number of parameters supplied for lambda declaration", which made the
    /// natural "no filters were selected" case fail rather than match everything.
    /// </summary>
    [Fact]
    public void EmptyAndMatchesEverything()
    {
        Assert.Equal(["Alice", "Bob", "Charlie", "David"], Matching(new ConjunctionCondition(Operator.And, [])));
    }

    [Fact]
    public void EmptyOrMatchesNothing()
    {
        // AND over no operands is true, OR over no operands is false, matching the identity of each operator
        // and the existing behaviour of IsIn/IsNotIn over an empty value list
        Assert.Empty(Matching(new ConjunctionCondition(Operator.Or, [])));
    }

    [Fact]
    public void EmptyAndNestedInsideAndIsIgnored()
    {
        var condition = new ConjunctionCondition(Operator.And, [Active(), new ConjunctionCondition(Operator.And, [])]);

        Assert.Equal(["Alice", "Bob", "David"], Matching(condition));
    }

    [Fact]
    public void EmptyOrNestedInsideAndExcludesEverything()
    {
        var condition = new ConjunctionCondition(Operator.And, [Active(), new ConjunctionCondition(Operator.Or, [])]);

        Assert.Empty(Matching(condition));
    }

    [Fact]
    public void EmptyAndNestedInsideOrIncludesEverything()
    {
        var condition = new ConjunctionCondition(Operator.Or, [Active(), new ConjunctionCondition(Operator.And, [])]);

        Assert.Equal(["Alice", "Bob", "Charlie", "David"], Matching(condition));
    }

    [Fact]
    public void NegatedEmptyConjunctionInverts()
    {
        Assert.Empty(Matching(new NotCondition(Operator.Not, new ConjunctionCondition(Operator.And, []))));
        Assert.Equal(["Alice", "Bob", "Charlie", "David"], Matching(new NotCondition(Operator.Not, new ConjunctionCondition(Operator.Or, []))));
    }

    [Fact]
    public void EmptyConjunctionWorksWithNoBindingsAtAll()
    {
        // Nothing bound means no shared parameter to borrow, which must still produce a usable lambda
        var result = MinionTestData.Minions()
            .WithWeequery()
            .ApplyCondition(new ConjunctionCondition(Operator.And, []))
            .Build()
            .Count();

        Assert.Equal(4, result);
    }

    [Fact]
    public void SingleOperandConjunctionBehavesAsThatOperand()
    {
        Assert.Equal(["Alice", "Bob", "David"], Matching(new ConjunctionCondition(Operator.And, [Active()])));
        Assert.Equal(["Alice", "Bob", "David"], Matching(new ConjunctionCondition(Operator.Or, [Active()])));
    }

    // ---------- pagination ----------

    private static string[] Page(int pageSize, int page)
    {
        return MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplySorts([new Sort(nameof(Minion.Pay), SortDirection.Ascending)])
            .ApplyPagination(pageSize, page)
            .Build()
            .ToList()
            .Select(minion => minion.Name.Split(' ')[0])
            .ToArray();
    }

    [Fact]
    public void PagesWalkTheOrderedResults()
    {
        // by ascending pay: Bob 0, David 8000, Alice 12000, Charlie 19000
        Assert.Equal(["Bob", "David"], Page(2, 0));
        Assert.Equal(["Alice", "Charlie"], Page(2, 1));
        Assert.Empty(Page(2, 2));
    }

    [Fact]
    public void PageSizeLargerThanTheResultSetReturnsEverything()
    {
        Assert.Equal(4, Page(100, 0).Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void NonPositivePageSizeIsRejected(int pageSize)
    {
        Assert.Throws<WeequeryException>(() => Page(pageSize, 0));
    }

    /// <summary>
    /// The page argument was never actually checked: the second guard tested pageSize a second time, so a
    /// negative page slipped through and became a negative Skip.
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(-5)]
    [InlineData(int.MinValue)]
    public void NegativePageIsRejected(int page)
    {
        Assert.Throws<WeequeryException>(() => Page(10, page));
    }

    /// <summary>
    /// The same failure as a negative page, arriving by a different route: each argument is in range, but the
    /// rows to skip is their product, and Skip takes an int. Left unchecked it wrapped to a negative skip, which
    /// does not fail it quietly answers with the first page instead of the one that was asked for.
    /// </summary>
    [Theory]
    [InlineData(2, int.MaxValue)]
    [InlineData(int.MaxValue, 2)]
    [InlineData(int.MaxValue, int.MaxValue)]
    [InlineData(100_000, 50_000)]
    [InlineData(46_341, 46_341)]
    public void APageSizeAndPageThatWouldOverflowTheSkipAreRejected(int pageSize, int page)
    {
        Assert.Throws<WeequeryException>(() => Page(pageSize, page));
    }

    /// <summary>
    /// Right up to the limit is still allowed: the check is on what cannot be counted, not on what is merely large
    /// </summary>
    [Theory]
    [InlineData(int.MaxValue, 1)]
    [InlineData(1, int.MaxValue)]
    [InlineData(2, int.MaxValue / 2)]
    [InlineData(46_340, 46_340)]
    public void ThePairsThatFitAreStillAccepted(int pageSize, int page)
    {
        // Past the end of four minions, so the answer is empty rather than an exception
        Assert.Empty(Page(pageSize, page));
    }

    [Fact]
    public void PaginationCombinesWithConditionsAndSorts()
    {
        var result = MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition("IsActive == true")
            .ApplySorts([new Sort(nameof(Minion.Pay), SortDirection.Descending)])
            .ApplyPagination(2, 0)
            .Build()
            .ToList();

        // actives by descending pay: Alice 12000, David 8000, Bob 0
        Assert.Equal(["Alice", "David"], result.Select(minion => minion.Name.Split(' ')[0]).ToArray());
    }

    // ---------- the predicate, without a query to hang it on ----------

    /// <summary>
    /// BuildExpression hands back the predicate itself rather than an IQueryable, so it composes with whatever
    /// the caller was already doing: Where, Any, or a Count over a list
    /// </summary>
    [Fact]
    public void BuildExpressionGivesAPredicateThatComposes()
    {
        var minions = MinionTestData.Minions();

        var predicate = Inquiry<Minion>.BuildExpression(Minion.Bindings, Active());

        Assert.True(minions.Any(predicate));
        Assert.Equal(3, minions.Where(predicate).Count());
    }
}
