using Tests.Common;
using Weequery;
using Weequery.Interfaces;

namespace Tests.Unit;

/// <summary>
/// The standalone builders keep the binding set they built, since resolving a property path is reflection and
/// doing it per call made a build cost more the more properties were bound.
/// <para>
/// A binding is immutable once made, so sharing one set across calls and threads is safe, but only if the set
/// really is decided by the requests alone. These pin that: the same requests reuse a set, different requests do
/// not share one, and each entity type keeps its own.
/// </para>
/// </summary>
public class BindingSetReuseTests
{
    private static readonly BindingRequest[] PayOnly = [new(nameof(Minion.Pay), null)];

    private static int Count(BindingRequest[] bindings, ICondition condition)
    {
        return MinionTestData.Minions().Count(Inquiry<Minion>.BuildDelegate(bindings, condition));
    }

    private static OneValueCondition<decimal> PayOver(decimal pay)
    {
        return new OneValueCondition<decimal>(Operator.GreaterThan, nameof(Minion.Pay), pay);
    }

    /// <summary>
    /// The reason to be careful: the second call gets the set the first one built, including its parameter and
    /// accessors, so a build that reuses one has to be as correct as a build that made its own
    /// </summary>
    [Fact]
    public void RepeatedBuildsWithTheSameRequestsAgree()
    {
        Assert.Equal(2, Count(PayOnly, PayOver(10000m)));
        Assert.Equal(2, Count(PayOnly, PayOver(10000m)));

        // and a different condition against the reused set still answers for itself
        Assert.Equal(3, Count(PayOnly, PayOver(1m)));
    }

    /// <summary>
    /// Keyed on what the requests say, not on which array said it, so a set built fresh each call still lands on
    /// the same bindings
    /// </summary>
    [Fact]
    public void AnEquivalentSetBuiltFreshBehavesTheSame()
    {
        BindingRequest[] fresh = [new(nameof(Minion.Pay), null)];

        Assert.Equal(Count(PayOnly, PayOver(10000m)), Count(fresh, PayOver(10000m)));
    }

    /// <summary>
    /// The same path bound under a different key is a different set, and must not be served the other one
    /// </summary>
    [Fact]
    public void TheSamePathUnderADifferentKeyIsADifferentSet()
    {
        BindingRequest[] asSalary = [new(nameof(Minion.Pay), "Salary")];

        Assert.Equal(2, Count(asSalary, new OneValueCondition<decimal>(Operator.GreaterThan, "Salary", 10000m)));

        // the key it was not bound under is unbound, whichever set was built first
        Assert.Throws<WeequeryException>(() => Count(asSalary, PayOver(10000m)));
        Assert.Throws<WeequeryException>(() => Count(PayOnly, new OneValueCondition<decimal>(Operator.GreaterThan, "Salary", 10000m)));
    }

    /// <summary>
    /// A list a caller keeps adding to describes a different set each time, so the key has to follow the contents
    /// rather than the instance
    /// </summary>
    [Fact]
    public void MutatingTheRequestsIsPickedUp()
    {
        List<BindingRequest> requests = [new(nameof(Minion.Pay), null)];

        var condition = new OneValueCondition<string>(Operator.Equals, nameof(Minion.Name), "Alice Fox");

        // Name is not bound yet
        Assert.Throws<WeequeryException>(() => Inquiry<Minion>.BuildDelegate(requests, condition));

        requests.Add(new(nameof(Minion.Name), null));

        Assert.Equal(1, MinionTestData.Minions().Count(Inquiry<Minion>.BuildDelegate(requests, condition)));
    }

    /// <summary>
    /// Two entity types can be handed requests that read the same. Each closed generic keeps its own sets, so
    /// "Name" on a Lair cannot be served a Minion's binding.
    /// </summary>
    [Fact]
    public void EachEntityTypeKeepsItsOwnSets()
    {
        BindingRequest[] nameOnly = [new("Name", null)];
        var condition = new OneValueCondition<string>(Operator.Equals, "Name", "Volcano");

        Assert.Equal(0, MinionTestData.Minions().Count(Inquiry<Minion>.BuildDelegate(nameOnly, condition)));

        var lairs = new List<Lair> { new() { Name = "Volcano" }, new() { Name = "Bunker" } };
        Assert.Equal(1, lairs.Count(Inquiry<Lair>.BuildDelegate(nameOnly, condition)));
    }

    /// <summary>
    /// A set is built once and read by everything after it, so the first build racing with the rest has to be
    /// safe. Uses a set of its own so this test is the one racing on it.
    /// </summary>
    [Fact]
    public void ConcurrentBuildsOnANewSetAgree()
    {
        BindingRequest[] requests = [new(nameof(Minion.Pay), "ConcurrentPay")];
        var condition = new OneValueCondition<decimal>(Operator.GreaterThan, "ConcurrentPay", 10000m);

        var counts = new int[64];
        Parallel.For(0, counts.Length, i => counts[i] = MinionTestData.Minions().Count(Inquiry<Minion>.BuildDelegate(requests, condition)));

        Assert.All(counts, count => Assert.Equal(2, count));
    }

    /// <summary>
    /// Requests that cannot be bound throw every time rather than the first failure being remembered as an answer
    /// </summary>
    [Fact]
    public void RequestsThatCannotBeBoundKeepThrowing()
    {
        BindingRequest[] bogus = [new("NotAProperty", null)];

        Assert.Throws<WeequeryException>(() => Inquiry<Minion>.BuildExpression(bogus, PayOver(1m)));
        Assert.Throws<WeequeryException>(() => Inquiry<Minion>.BuildExpression(bogus, PayOver(1m)));
    }

    // ---------- the fluent path shares the same sets ----------

    /// <summary>
    /// The risk in reusing a set through BindProperties: a binding brings the parameter it was built against, and
    /// a lambda is built from one binding's parameter with a body assembled from several. If a reused binding and
    /// a freshly bound one disagreed on the parameter, a condition touching both would build a lambda whose body
    /// referred to something its signature did not declare. These mix the two routes in both orders.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReusedAndFreshlyBoundPropertiesCombineInOneCondition(bool requestsFirst)
    {
        BindingRequest[] requests = [new(nameof(Minion.Pay), null)];

        var inquiry = MinionTestData.Minions().WithWeequery();

        if (requestsFirst)
        {
            inquiry.BindProperties(requests).BindProperty(minion => minion.IsActive);
        }
        else
        {
            inquiry.BindProperty(minion => minion.IsActive).BindProperties(requests);
        }

        // One condition over both, so the two bindings have to compose
        var result = inquiry
            .ApplyCondition("(Pay > 1000) && (IsActive == true)")
            .Build()
            .ToList();

        Assert.Equal(2, result.Count);
    }

    /// <summary>
    /// A sort lambda is built from the binding's accessor and the parameter separately, so it is the other place a
    /// mismatch would surface
    /// </summary>
    [Fact]
    public void AReusedBindingCanBeSortedOnAlongsideAFreshOne()
    {
        var result = MinionTestData.Minions()
            .WithWeequery()
            .BindProperties([new(nameof(Minion.Pay), null)])
            .BindProperty(minion => minion.IsActive)
            .ApplySorts([new Sort(nameof(Minion.IsActive), SortDirection.Ascending), new Sort(nameof(Minion.Pay), SortDirection.Descending)])
            .Build()
            .ToList();

        Assert.Equal("Charlie Smith", result.First().Name);
        Assert.Equal(4, result.Count);
    }

    /// <summary>
    /// Reuse does not make a duplicate key acceptable, whichever route claimed it first
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ADuplicateAcrossTheTwoRoutesIsStillRefused(bool requestsFirst)
    {
        var inquiry = MinionTestData.Minions().WithWeequery();

        Assert.Throws<WeequeryException>(() => (requestsFirst)
            ? inquiry.BindProperties([new(nameof(Minion.Pay), null)]).BindProperty(minion => minion.Pay)
            : inquiry.BindProperty(minion => minion.Pay).BindProperties([new(nameof(Minion.Pay), null)]));
    }

    /// <summary>
    /// The fluent route and the standalone one now draw on the same sets, so they must answer alike
    /// </summary>
    [Fact]
    public void TheFluentAndStandaloneRoutesAgree()
    {
        var condition = PayOver(10000m);

        var fluent = MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition(condition)
            .Build()
            .Count();

        Assert.Equal(fluent, Count(Minion.Bindings, condition));
    }

    /// <summary>
    /// Two queries over the same type are two lambdas, built over one parameter and independent of each other
    /// </summary>
    [Fact]
    public void TwoQueriesOverTheSameTypeDoNotInterfere()
    {
        var minions = MinionTestData.Minions();

        var rich = minions.WithWeequery().BindProperties(Minion.Bindings).ApplyCondition(PayOver(10000m)).Build();
        var poor = minions.WithWeequery().BindProperties(Minion.Bindings).ApplyCondition("Pay < 1000").Build();

        Assert.Equal(2, rich.Count());
        Assert.Equal(1, poor.Count());

        // and enumerating one after the other does not change either
        Assert.Equal(2, rich.Count());
    }

    /// <summary>
    /// BuildDelegate is BuildExpression compiled, so the two have to agree on the rows
    /// </summary>
    [Fact]
    public void TheExpressionAndTheDelegateAgree()
    {
        var condition = PayOver(10000m);

        var byExpression = MinionTestData.Minions().Where(Inquiry<Minion>.BuildExpression(Minion.Bindings, condition)).Count();
        var byDelegate = MinionTestData.Minions().Count(Inquiry<Minion>.BuildDelegate(Minion.Bindings, condition));

        Assert.Equal(byExpression, byDelegate);
    }
}
