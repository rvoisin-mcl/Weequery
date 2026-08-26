using System.Text.Json;
using Tests.Common;
using Weequery;
using Weequery.Interfaces;

namespace Tests.Unit;

/// <summary>
/// A collection handed to Weequery can carry a hole where a sort or a condition should be, and a sort can arrive
/// without the field it sorts on. Both are what an ordinary JSON body produces, and the documented request DTO
/// passes its list straight through, so both used to reach the query as a framework exception: a
/// NullReferenceException for the hole, an ArgumentNullException for the missing field, from inside Build.
/// <para>
/// Nothing at all is still nothing to do. A hole among something is a mistake, and is named.
/// </para>
/// </summary>
public class MissingElementTests
{
    private static Inquiry<Minion> Query()
    {
        return MinionTestData.Minions().WithWeequery().BindProperties(Minion.Bindings);
    }

    // ---------- sorts ----------

    /// <summary>
    /// "sorts": [null] deserializes to a list holding a null, which is what the controller in the README would
    /// hand over
    /// </summary>
    [Fact]
    public void ANullSortOffTheWireIsRefused()
    {
        var sorts = JsonSerializer.Deserialize<List<Sort>>("[null]")!;

        Assert.Throws<WeequeryException>(() => Query().ApplySorts(sorts));
    }

    /// <summary>
    /// And a sort whose field was simply left out, which deserializes to a Sort with no field rather than to null
    /// </summary>
    [Fact]
    public void ASortWithNoFieldOffTheWireIsRefused()
    {
        var sorts = JsonSerializer.Deserialize<List<Sort>>("""[{"Direction":1}]""")!;

        Assert.Throws<WeequeryException>(() => Query().ApplySorts(sorts));
    }

    [Fact]
    public void TheHoleIsNamedByItsPosition()
    {
        var sorts = new List<Sort> { new(nameof(Minion.Pay), SortDirection.Ascending), null! };

        Assert.Throws<WeequeryException>(() => Query().ApplySorts(sorts));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ASortWithNoFieldIsRefusedOneAtATimeToo(string? field)
    {
        Assert.Throws<WeequeryException>(() => Query().ApplySort(new Sort(field!, SortDirection.Ascending)));
    }

    // ---------- conditions ----------

    [Fact]
    public void ANullConditionAmongOthersIsRefused()
    {
        List<ICondition> conditions = [new OneValueCondition<decimal>(Operator.GreaterThan, nameof(Minion.Pay), 1m), null!];

        Assert.Throws<WeequeryException>(() => Query().ApplyConditions(conditions));
    }

    [Fact]
    public void ANullChildOfAConjunctionIsRefusedWhenItIsBuilt()
    {
        Assert.Throws<WeequeryException>(() => new ConjunctionCondition(Operator.And,
        [
            new OneValueCondition<decimal>(Operator.GreaterThan, nameof(Minion.Pay), 1m),
            null!,
        ]));
    }

    /// <summary>
    /// The lists stay the caller's to change, so the walk says so rather than dereferencing a hole put there
    /// afterwards
    /// </summary>
    [Fact]
    public void AChildNulledOutAfterwardsIsRefusedAtBuild()
    {
        var conjunction = new ConjunctionCondition(Operator.And, [new OneValueCondition<decimal>(Operator.GreaterThan, nameof(Minion.Pay), 1m)]);

        conjunction.Conditions[0] = null!;

        Assert.Throws<WeequeryException>(() => Query().ApplyCondition(conjunction).Build());
    }

    // ---------- nothing at all is still nothing to do ----------

    [Fact]
    public void TheEmptyAndAbsentCasesStillPassThrough()
    {
        Assert.Equal(4, Query().ApplySort(null).ApplySorts(null).ApplyCondition((ICondition?)null).ApplyConditions(null).Build().Count());
        Assert.Equal(4, Query().ApplySorts([]).ApplyConditions([]).Build().Count());
    }

    /// <summary>
    /// And a list that is entirely well formed is unaffected
    /// </summary>
    [Fact]
    public void AWellFormedListStillSorts()
    {
        var sorts = JsonSerializer.Deserialize<List<Sort>>($$"""[{"Field":"{{nameof(Minion.Pay)}}","Direction":1}]""")!;

        var sorted = Query().ApplySorts(sorts).Build().ToList();

        Assert.Equal(19000m, sorted.First().Pay);
    }
}
