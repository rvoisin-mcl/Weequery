using Tests.Common;
using Weequery;
using Weequery.Interfaces;

namespace Tests.Unit;

/// <summary>
/// The fluent helpers on a conjunction, and how each of them can compare against another bound property instead of
/// against a value.
/// <para>
/// The single value helpers carry a <see cref="ValueSource"/> that defaults to <see cref="ValueSource.Raw"/>, so
/// the ordinary call is unchanged and naming a property is one extra argument. The range family has a second
/// overload taking a source per end, since either end can be a property, and the membership family has one taking
/// <see cref="ConditionValue{T}"/> operands, since a list can mix them freely.
/// </para>
/// </summary>
public class FluentConditionTests
{
    private static IConjunctionCondition Conjunction()
    {
        return new ConjunctionCondition(Operator.And, []);
    }

    /// <summary>
    /// The single condition a conjunction was given, as the shape it should have landed on
    /// </summary>
    private static TCondition Only<TCondition>(IConjunctionCondition conjunction) where TCondition : class
    {
        return Assert.IsType<TCondition>(Assert.Single(conjunction.Conditions));
    }

    private static string[] Matching(ICondition condition)
    {
        return [.. MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .BindConstant("Floor", 8000m)
            .BindConstant("Ceiling", 12000m)
            .ApplyCondition(condition)
            .Build()
            .Select(minion => minion.Name.Split(' ')[0])
            .ToList()
            .Order()];
    }

    // ---------- the ordinary call, unchanged ----------

    [Fact]
    public void TheValuelessHelpersAddANoValueCondition()
    {
        Assert.Equal(Operator.IsNull, Only<NoValueCondition>(Conjunction().AddIsNullTest(nameof(Minion.Alias))).Operator);
        Assert.Equal(Operator.IsNotNull, Only<NoValueCondition>(Conjunction().AddIsNotNullTest(nameof(Minion.Alias))).Operator);
    }

    /// <summary>
    /// A value alone is a value: the source defaults, and T is the value's own type rather than anything wrapping it
    /// </summary>
    [Fact]
    public void TheSingleValueHelpersAddAOneValueConditionOfTheValuesOwnType()
    {
        var condition = Only<OneValueCondition<decimal>>(Conjunction().AddIsGreaterThanTest(nameof(Minion.Pay), 10000m));

        Assert.Equal(Operator.GreaterThan, condition.Operator);
        Assert.Equal(nameof(Minion.Pay), condition.Field);
        Assert.Equal(ConditionValue.Raw(10000m), condition.Value);
    }

    [Fact]
    public void TheRangeHelpersAddATwoValueCondition()
    {
        var condition = Only<TwoValueCondition<decimal>>(Conjunction().AddIsBetweenTest(nameof(Minion.Pay), 8000m, 12000m));

        Assert.Equal(Operator.IsBetween, condition.Operator);
        Assert.Equal(ConditionValue.Raw(8000m), condition.Value1);
        Assert.Equal(ConditionValue.Raw(12000m), condition.Value2);
    }

    [Fact]
    public void TheListHelpersAddAMultipleValueCondition()
    {
        var condition = Only<MultipleValueCondition<decimal>>(Conjunction().AddIsInTest(nameof(Minion.Pay), [8000m, 12000m]));

        Assert.Equal(Operator.IsIn, condition.Operator);
        Assert.Equal([ConditionValue.Raw(8000m), ConditionValue.Raw(12000m)], condition.Values);
    }

    [Fact]
    public void TheSubstringHelpersAddAOneValueConditionOverString()
    {
        Assert.Equal(Operator.StartsWith, Only<OneValueCondition<string>>(Conjunction().AddStartsWithTest(nameof(Minion.Name), "Al")).Operator);
        Assert.Equal(Operator.DoesNotStartWith, Only<OneValueCondition<string>>(Conjunction().AddDoesNotStartWithTest(nameof(Minion.Name), "Al")).Operator);
        Assert.Equal(Operator.EndsWith, Only<OneValueCondition<string>>(Conjunction().AddEndsWithTest(nameof(Minion.Name), "Fox")).Operator);
        Assert.Equal(Operator.DoesNotEndWith, Only<OneValueCondition<string>>(Conjunction().AddDoesNotEndWithTest(nameof(Minion.Name), "Fox")).Operator);
        Assert.Equal(Operator.Contains, Only<OneValueCondition<string>>(Conjunction().AddContainsTest(nameof(Minion.Name), "ice")).Operator);
        Assert.Equal(Operator.DoesNotContain, Only<OneValueCondition<string>>(Conjunction().AddDoesNotContainTest(nameof(Minion.Name), "ice")).Operator);
    }

    // ---------- naming a property instead ----------

    /// <summary>
    /// One extra argument, and the value is read as a key. A key is a name, so such a call is over string whatever
    /// the property it names holds.
    /// </summary>
    [Fact]
    public void ASourceOfBindingMakesTheValueAKey()
    {
        var condition = Only<OneValueCondition<string>>(Conjunction().AddIsLessThanTest(nameof(Minion.HireDate), nameof(Minion.FireDate), ValueSource.Binding));

        Assert.True(condition.Value.NamesProperty);
        Assert.Equal("([HireDate] < [FireDate])", condition.ToQuery());
    }

    /// <summary>
    /// And it filters as the written form of the same comparison does. Bob is the only one who was fired.
    /// </summary>
    [Fact]
    public void AnOperandNamingAPropertyFiltersAsTheQueryWouldHave()
    {
        var built = Conjunction().AddIsLessThanTest(nameof(Minion.HireDate), nameof(Minion.FireDate), ValueSource.Binding);

        Assert.Equal(["Bob"], Matching(built));
        Assert.Equal(Matching(ConditionFunctions.ParseQuery("HireDate < [FireDate]")!), Matching(built));
    }

    /// <summary>
    /// A substring operator takes a source too, so "whose name contains their own currency" is sayable
    /// </summary>
    [Fact]
    public void ASubstringOperatorTakesASourceAsWell()
    {
        var built = Conjunction().AddContainsTest(nameof(Minion.Name), nameof(Minion.PreferredCurrency), ValueSource.Binding);

        Assert.Equal("([Name] Contains [PreferredCurrency])", Assert.Single(built.Conditions).ToQuery());
        Assert.Empty(Matching(built));
    }

    // ---------- a range, where either end can be a property ----------

    [Fact]
    public void EitherEndOfARangeCanBeAProperty()
    {
        var condition = Only<TwoValueCondition<string>>(Conjunction().AddIsBetweenTest(nameof(Minion.Pay), "8000", ValueSource.Raw, "Ceiling", ValueSource.Binding));

        Assert.False(condition.Value1.NamesProperty);
        Assert.True(condition.Value2.NamesProperty);
        Assert.Equal("([Pay] IsBetween ('8000', [Ceiling]))", condition.ToQuery());
    }

    [Fact]
    public void AMixedRangeFilters()
    {
        // Floor and Ceiling are bound constants, 8000 and 12000
        Assert.Equal(["Alice", "David"], Matching(Conjunction().AddIsBetweenTest(nameof(Minion.Pay), "8000", ValueSource.Raw, "Ceiling", ValueSource.Binding)));
        Assert.Equal(["Bob", "Charlie"], Matching(Conjunction().AddIsNotBetweenTest(nameof(Minion.Pay), "Floor", ValueSource.Binding, "12000", ValueSource.Raw)));
    }

    // ---------- a list, which can mix them ----------

    /// <summary>
    /// A list carries a source per operand, so it takes <see cref="ConditionValue{T}"/> rather than one source for
    /// all of them. The plain list overload is still there and reads every member as a value.
    /// </summary>
    [Fact]
    public void AListOfConditionValuesReachesTheOverloadThatTakesOne()
    {
        var list = Only<MultipleValueCondition<decimal>>(Conjunction().AddIsInTest(nameof(Minion.Pay), [ConditionValue.Raw(8000m)]));

        Assert.Equal([ConditionValue.Raw(8000m)], list.Values);
    }

    [Fact]
    public void AListCanMixValuesAndProperties()
    {
        var built = Conjunction().AddIsInTest(nameof(Minion.Name), [ConditionValue.Raw("Charlie Smith"), ConditionValue.Binding(nameof(Minion.Alias))]);

        Assert.Equal("([Name] IsIn ('Charlie Smith', [Alias]))", Assert.Single(built.Conditions).ToQuery());
        Assert.Equal(["Charlie"], Matching(built));
    }

    // ---------- what a key cannot be ----------

    /// <summary>
    /// A key is a name whatever the property holds, so asking for one over some other type is refused where the
    /// mistake was made rather than at the far end of a round trip
    /// </summary>
    [Fact]
    public void AKeyAskedForOverSomethingOtherThanTextIsRefused()
    {
        Assert.Throws<WeequeryException>(() => Conjunction().AddIsEqualTest(nameof(Minion.Pay), 10000m, ValueSource.Binding));
    }

    // ---------- chaining ----------

    /// <summary>
    /// Every helper returns the conjunction, and the two forms chain together
    /// </summary>
    [Fact]
    public void TheHelpersChainInEitherForm()
    {
        var conjunction = Conjunction()
            .AddIsGreaterThanTest(nameof(Minion.Pay), 0m)
            .AddIsLessThanTest(nameof(Minion.HireDate), nameof(Minion.FireDate), ValueSource.Binding)
            .AddIsNotNullTest(nameof(Minion.CauseForDeparture));

        Assert.Equal(3, conjunction.Conditions.Count);
        Assert.Empty(Matching(conjunction)); // Bob was fired, but his pay is 0
    }
}
