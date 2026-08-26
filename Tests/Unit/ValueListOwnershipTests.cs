using Tests.Common;
using Weequery;
using Weequery.Interfaces;

namespace Tests.Unit;

/// <summary>
/// A condition owns its values. It used to keep the list it was handed, so the count checked at construction was
/// not necessarily the count that reached the provider: a caller holding the same list could add to it afterwards
/// and walk straight past the IsIn cap.
/// </summary>
public class ValueListOwnershipTests
{
    private const int MaxValuesInList = 1000;

    private static List<decimal> Values(int count)
    {
        return Enumerable.Range(0, count).Select(i => (decimal)i).ToList();
    }

    private static IQueryable<Minion> Minions()
    {
        return MinionTestData.Minions();
    }

    [Fact]
    public void AddingToTheListAfterwardsDoesNotChangeTheCondition()
    {
        var values = new List<decimal> { 12000m };

        var condition = new MultipleValueCondition<decimal>(Operator.IsIn, nameof(Minion.Pay), values);

        values.Add(8000m);

        Assert.Single(condition.Values);
        Assert.Equal(1, Minions().WithWeequery().BindProperties(Minion.Bindings).ApplyCondition(condition).Build().Count());
    }

    /// <summary>
    /// The route the cap was walked past: check at construction, grow afterwards
    /// </summary>
    [Fact]
    public void TheCapCannotBeWalkedPastByGrowingTheListPassedIn()
    {
        var values = Values(1);

        var condition = new MultipleValueCondition<decimal>(Operator.IsIn, nameof(Minion.Pay), values);

        values.AddRange(Values(MaxValuesInList * 5));

        Assert.Single(condition.Values);
    }

    /// <summary>
    /// And if it is grown through the condition itself, which the type still allows, the build refuses it: what
    /// the condition holds then is what would become parameters
    /// </summary>
    [Fact]
    public void ACapExceededAfterConstructionIsRefusedWhenTheQueryIsBuilt()
    {
        var condition = new MultipleValueCondition<decimal>(Operator.IsIn, nameof(Minion.Pay), Values(MaxValuesInList));

        condition.Values.Add(ConditionValue.Raw(1m));

        Assert.Throws<WeequeryException>(() => Minions().WithWeequery().BindProperties(Minion.Bindings).ApplyCondition(condition).Build());
    }

    /// <summary>
    /// Packing takes a snapshot rather than a second reference to the same list
    /// </summary>
    [Fact]
    public void PackingDoesNotShareTheConditionsList()
    {
        var condition = new MultipleValueCondition<string>(Operator.IsIn, nameof(Minion.Alias), ["Ghost"]);

        var packed = condition.Pack();
        packed.Values.Add(ConditionValue.Raw("Snake"));

        Assert.Single(condition.Values);
        Assert.Equal(2, packed.Values.Count);
    }

    [Fact]
    public void StringifyingDoesNotHandOutTheConditionsList()
    {
        var condition = new MultipleValueCondition<string>(Operator.IsIn, nameof(Minion.Alias), ["Ghost"]);

        condition.StringifyValues().Add("Snake");

        Assert.Single(condition.Values);
    }

    /// <summary>
    /// A conjunction owns its children the same way
    /// </summary>
    [Fact]
    public void AConjunctionDoesNotKeepTheListItWasHanded()
    {
        List<ICondition> components = [new OneValueCondition<decimal>(Operator.GreaterThan, nameof(Minion.Pay), 10000m)];

        var conjunction = new ConjunctionCondition(Operator.And, components);

        components.Add(new OneValueCondition<bool>(Operator.Equals, nameof(Minion.IsActive), true));

        Assert.Single(conjunction.Conditions);
        Assert.Equal(2, Minions().WithWeequery().BindProperties(Minion.Bindings).ApplyCondition(conjunction).Build().Count());
    }

    /// <summary>
    /// Copying the values must not change what a condition means
    /// </summary>
    [Fact]
    public void TheValuesStillSelectTheSameRows()
    {
        var condition = new MultipleValueCondition<decimal>(Operator.IsIn, nameof(Minion.Pay), [12000m, 8000m]);

        var names = Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition(condition)
            .Build()
            .Select(minion => minion.Name.Split(' ')[0])
            .Order()
            .ToArray();

        Assert.Equal(["Alice", "David"], names);
    }
}
