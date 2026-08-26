using Microsoft.EntityFrameworkCore;
using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// A binding can stand for a value the application supplied rather than a property of the row, bound under a name
/// the caller can refer to but cannot set.
/// <para>
/// The pairing it exists for is a comparison against a bound property: the caller writes "Pay &gt; [Threshold]" and
/// the application decides what Threshold is, per request or per tenant.
/// </para>
/// </summary>
public class ConstantBindingTests
{
    private static string[] Matching(decimal threshold, string query)
    {
        return MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .BindConstant("Threshold", threshold)
            .ApplyCondition(query)
            .Build()
            .ToList()
            .Select(minion => minion.Name.Split(' ')[0])
            .Order()
            .ToArray();
    }

    // Alice 12000, Bob 0, Charlie 19000, David 8000

    [Fact]
    public void APropertyComparesAgainstAConstant()
    {
        Assert.Equal(["Alice", "Charlie"], Matching(10000m, "Pay > [Threshold]"));
        Assert.Equal(["Bob", "David"], Matching(10000m, "Pay <= [Threshold]"));
    }

    /// <summary>
    /// The point of it being a binding rather than a value in the query: the same query, a different answer,
    /// decided by the application
    /// </summary>
    [Fact]
    public void TheApplicationDecidesWhatItIs()
    {
        Assert.Equal(["Alice", "Charlie"], Matching(10000m, "Pay > [Threshold]"));
        Assert.Equal(["Charlie"], Matching(12000m, "Pay > [Threshold]"));
        Assert.Empty(Matching(19000m, "Pay > [Threshold]"));
    }

    /// <summary>
    /// Whatever the type, including the ones that needed their own handling for a comparison: a date, an enum, a
    /// string
    /// </summary>
    [Fact]
    public void AnyOfTheSupportedTypesCanBeAConstant()
    {
        var query = MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .BindConstant("Cutoff", new DateTime(2020, 1, 1))
            .BindConstant("Floor", Classification.Irreplacable)
            .BindConstant("Prefix", "Al");

        // Three were hired in 2018; David in 2025
        Assert.Equal(3, query.ApplyCondition("HireDate < [Cutoff]").Build().Count());

        var byClass = MinionTestData.Minions().WithWeequery().BindProperties(Minion.Bindings)
            .BindConstant("Floor", Classification.Irreplacable).ApplyCondition("Classification >= [Floor]").Build().ToList();
        Assert.Equal(["David Edgars"], byClass.Select(minion => minion.Name));

        var byPrefix = MinionTestData.Minions().WithWeequery().BindProperties(Minion.Bindings)
            .BindConstant("Prefix", "Al").ApplyCondition("Name StartsWith [Prefix]").Build().ToList();
        Assert.Equal(["Alice Fox"], byPrefix.Select(minion => minion.Name));
    }

    /// <summary>
    /// It reads as a field on the left as well, which is legal if not often useful: every row answers the same
    /// </summary>
    [Fact]
    public void ItCanBeTheFieldAsWell()
    {
        Assert.Equal(4, Matching(10000m, "Threshold == 10000").Length);
        Assert.Empty(Matching(10000m, "Threshold == 9999"));
    }

    // ---------- what is refused ----------

    [Fact]
    public void SortingOnAConstantIsRefused()
    {
        Assert.Throws<WeequeryException>(() => MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .BindConstant("Threshold", 10000m)
            .ApplySort(new Sort("Threshold", SortDirection.Ascending))
            .Build());
    }

    [Fact]
    public void AConstantWithNoValueIsRefused()
    {
        Assert.Throws<WeequeryException>(() => MinionTestData.Minions().WithWeequery().BindConstant<string>("Nope", null!));
    }

    /// <summary>
    /// A constant is bound under a key like anything else, so the key rules apply to it
    /// </summary>
    [Fact]
    public void TheKeyRulesApply()
    {
        Assert.Throws<WeequeryException>(() => MinionTestData.Minions().WithWeequery().BindConstant("Contains", 1m));
        Assert.Throws<WeequeryException>(() => MinionTestData.Minions().WithWeequery().BindConstant("my constant", 1m));
        Assert.Throws<WeequeryException>(() => MinionTestData.Minions().WithWeequery().BindProperties(Minion.Bindings).BindConstant(nameof(Minion.Pay), 1m));
    }

    /// <summary>
    /// The same type rule a property to property comparison follows: the application controls the constant's type,
    /// so it is the one that has to match
    /// </summary>
    [Fact]
    public void ComparingAgainstAConstantOfAnotherTypeIsRefused()
    {
        Assert.Throws<WeequeryException>(() => MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .BindConstant("Threshold", 10000)      // an int, against a decimal property
            .ApplyCondition("Pay > [Threshold]")
            .Build());
    }

    // ---------- against a provider ----------

    /// <summary>
    /// The value goes to the database as a parameter, so the statement is the same whatever it is: one plan for
    /// every threshold, the same property values written into a condition have
    /// </summary>
    [Theory]
    [MemberData(nameof(TestDatabase.AllProviders), MemberType = typeof(TestDatabase))]
    public void TheValueIsParameterized(TestProvider provider)
    {
        using var context = TestDatabase.Create(provider);

        string Statement(decimal threshold)
        {
            return TestDatabase.StatementOnly(context.Minions
                .WithWeequery()
                .BindProperties(Minion.Bindings)
                .BindConstant("Threshold", threshold)
                .ApplyCondition("Pay > [Threshold]")
                .Build()
                .ToQueryString());
        }

        var one = Statement(10000m);

        Assert.Contains("@Value", one);
        Assert.DoesNotContain("10000", one);

        // and the statement does not change with the value
        Assert.Equal(one, Statement(12000m));
    }

    [Fact]
    public void TheDatabaseAnswersWithTheRightRows()
    {
        var context = TestDatabase.CreateSeeded(TestProvider.Sqlite);
        try
        {
            var names = context.Minions
                .WithWeequery()
                .BindProperties(Minion.Bindings)
                .BindConstant("Threshold", 10000m)
                .ApplyCondition("Pay > [Threshold]")
                .Build()
                .Select(minion => minion.Name)
                .ToList();

            Assert.Equal(["Alice Fox", "Charlie Smith"], names.Order());
        }
        finally
        {
            TestDatabase.Drop(context);
        }
    }

    /// <summary>
    /// A condition off the wire is what this is for, so the condition naming the constant can be a packed one
    /// </summary>
    [Fact]
    public void AConditionOffTheWireCanNameOne()
    {
        var transport = new TransportCondition("Pay > [Threshold]");

        var count = MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .BindConstant("Threshold", 10000m)
            .ApplyCondition(transport.Unpack())
            .Build()
            .Count();

        Assert.Equal(2, count);
    }
}
