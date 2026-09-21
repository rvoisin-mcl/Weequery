using Tests.Common;
using Weequery;
using Weequery.Interfaces;

namespace Tests.Unit;

/// <summary>
/// Which operators the thing running the query can run, and what happens to a condition using one it cannot.
/// <para>
/// The allow-list decides which fields a caller may name. This decides which operators they may use on them,
/// and the two are different questions: <see cref="Operator.IsMatch"/> against SQL Server names a bound field,
/// parses, builds, and is refused by the provider a long way from the request that carried it.
/// </para>
/// </summary>
public class OperatorSupportTests
{
    /// <summary>What SQL Server could not do until it could: no regular expressions</summary>
    private static readonly OperatorSupport NoRegex = OperatorSupport.Without(Operator.IsMatch, Operator.DoesNotMatch);

    private static Inquiry<Minion> Bound(OperatorSupport? operators = null)
    {
        return MinionTestData.Minions()
            .WithWeequery(InquirySettings.Default with { Operators = operators })
            .BindProperties(Minion.Bindings);
    }

    private static Inquiry<Crew> BoundCrews(OperatorSupport? operators = null)
    {
        return new List<Crew>
        {
            new() { Id = 1, Name = "Alpha", Heists = [new() { Target = "Bank", Take = 500 }] },
        }
        .AsQueryable()
        .WithWeequery(InquirySettings.Default with { Operators = operators })
        .BindProperty(crew => crew.Name)
        .BindCollection(crew => crew.Heists, "Heists", inner => inner
            .BindProperty(heist => heist.Target)
            .BindProperty(heist => heist.Take));
    }

    // ---------- the set itself ----------

    [Fact]
    public void EverythingAllowsEveryOperator()
    {
        Assert.True(OperatorSupport.Everything.IsEverything);

        foreach (var op in Enum.GetValues<Operator>()) { Assert.True(OperatorSupport.Everything.Allows(op)); }
    }

    [Fact]
    public void WithoutIsEverythingElse()
    {
        Assert.False(NoRegex.Allows(Operator.IsMatch));
        Assert.False(NoRegex.Allows(Operator.DoesNotMatch));

        Assert.True(NoRegex.Allows(Operator.Equals));
        Assert.True(NoRegex.Allows(Operator.And));
        Assert.True(NoRegex.Allows(Operator.Any));

        Assert.False(NoRegex.IsEverything);
    }

    /// <summary>
    /// A literal list, which is the awkward half of the pair and is meant to be: naming what a backend supports
    /// means naming the conjunctions too, since something has to evaluate those as well
    /// </summary>
    [Fact]
    public void SupportingIsLiteral()
    {
        var only = OperatorSupport.Supporting(Operator.Equals);

        Assert.True(only.Allows(Operator.Equals));
        Assert.False(only.Allows(Operator.And));
        Assert.False(only.Allows(Operator.NotEqual));
    }

    [Fact]
    public void TheEmptyCasesAreTheTwoExtremes()
    {
        Assert.True(OperatorSupport.Without().IsEverything);

        var nothing = OperatorSupport.Supporting();

        Assert.False(nothing.IsEverything);
        foreach (var op in Enum.GetValues<Operator>()) { Assert.False(nothing.Allows(op)); }
    }

    /// <summary>
    /// The same operators are the same set however it was arrived at, which is what lets an InquirySettings
    /// compare by value the way the rest of it does
    /// </summary>
    [Fact]
    public void TwoSetsHoldingTheSameOperatorsAreEqual()
    {
        var spelled = OperatorSupport.Supporting(Enum.GetValues<Operator>().Where(op => op != Operator.IsMatch));

        Assert.Equal(OperatorSupport.Without(Operator.IsMatch), spelled);
        Assert.Equal(OperatorSupport.Without(Operator.IsMatch).GetHashCode(), spelled.GetHashCode());

        Assert.NotEqual(OperatorSupport.Without(Operator.IsMatch), NoRegex);
    }

    [Fact]
    public void DuplicatesAndOrderDoNotMatter()
    {
        Assert.Equal(
            OperatorSupport.Without(Operator.IsMatch, Operator.DoesNotMatch),
            OperatorSupport.Without(Operator.DoesNotMatch, Operator.IsMatch, Operator.IsMatch));
    }

    [Fact]
    public void NullIsRefused()
    {
        Assert.Throws<WeequeryException>(() => OperatorSupport.Supporting((IEnumerable<Operator>)null!));
        Assert.Throws<WeequeryException>(() => OperatorSupport.Without((IEnumerable<Operator>)null!));
    }

    /// <summary>
    /// Null on the settings is the everything a caller who named no backend meant, on a with as on a ctor
    /// </summary>
    [Fact]
    public void SettingsWithoutOneSupportEverything()
    {
        Assert.True(InquirySettings.Default.Operators.IsEverything);

        var cleared = InquirySettings.Default with { Operators = NoRegex } with { Operators = null };

        Assert.NotNull(cleared.Operators);
        Assert.True(cleared.Operators.IsEverything);
    }

    // ---------- what a condition is found to use ----------

    /// <summary>
    /// Every operator, not only the comparisons. And, Or and Not are operators, and whatever runs the query has
    /// to run them too
    /// </summary>
    [Fact]
    public void OperatorsUsedCountsTheStructureAsWellAsTheComparisons()
    {
        var condition = ConditionFunctions.ParseQuery("Name = 'Alice Fox' AND NOT (Pay > 1)");

        Assert.Equal(
            [Operator.And, Operator.Equals, Operator.Not, Operator.GreaterThan],
            condition.OperatorsUsed());
    }

    [Fact]
    public void EachOperatorComesBackOnceHoweverOftenItIsUsed()
    {
        var condition = ConditionFunctions.ParseQuery("Name = 'a' OR Name = 'b' OR Name = 'c'");

        Assert.Equal([Operator.Or, Operator.Equals], condition.OperatorsUsed());
    }

    /// <summary>
    /// The one difference from FieldsUsed, and the reason both exist. A quantifier's children are a different
    /// allow-list, so fields stop at the boundary; they are not a different backend, so operators do not.
    /// </summary>
    [Fact]
    public void OperatorsUsedGoesInsideAQuantifierWhereFieldsUsedStops()
    {
        var condition = ConditionFunctions.ParseQuery("Heists Any (Target IsMatch 'Ban')");

        Assert.Equal([Operator.Any, Operator.IsMatch], condition.OperatorsUsed());
        Assert.Equal(["Heists"], condition.FieldsUsed());
    }

    [Fact]
    public void NothingUsesNothing()
    {
        Assert.Empty(((ICondition?)null).OperatorsUsed());
    }

    // ---------- where it is enforced ----------

    [Fact]
    public void TheDefaultRefusesNothing()
    {
        Assert.True(Bound().ApplyCondition("Name IsMatch '^A'").Validate().IsValid);
        Assert.Single(Bound().ApplyCondition("Name IsMatch '^A'").Build().ToList());
    }

    [Fact]
    public void ABuildRefusesAnOperatorTheBackendDoesNotHave()
    {
        var error = Assert.Throws<WeequeryException>(() => Bound(NoRegex).ApplyCondition("Name IsMatch '^A'").Build());

        Assert.Equal(WeequeryError.NotTranslatable, error.Error);
        Assert.Contains("Name", error.Message, StringComparison.Ordinal);
        Assert.Contains("IsMatch", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The contract Validate has always had: everything Build would refuse, reported rather than raised. It is
    /// the same refusal, so it is the same message
    /// </summary>
    [Fact]
    public void ValidateReportsWhatTheBuildWouldThrow()
    {
        var inquiry = Bound(NoRegex).ApplyCondition("Name IsMatch '^A'");

        var problem = Assert.Single(inquiry.Validate().Problems);

        Assert.Equal(BindingUse.Test, problem.Part);
        Assert.Equal(WeequeryError.NotTranslatable, problem.Error);
        Assert.Equal(Assert.Throws<WeequeryException>(() => inquiry.Build()).Message, problem.Message);
    }

    [Fact]
    public void ARequestIsValidatedTheSameWay()
    {
        var request = new QueryRequest { Filter = "Name IsMatch '^A'" };

        var problem = Assert.Single(Bound(NoRegex).Validate(request).Problems);

        Assert.Equal(BindingUse.Test, problem.Part);
        Assert.Equal(WeequeryError.NotTranslatable, problem.Error);
    }

    [Fact]
    public void EverythingElseStillBuilds()
    {
        var inquiry = Bound(NoRegex).ApplyCondition("Name = 'Alice Fox' AND Pay > 0");

        Assert.True(inquiry.Validate().IsValid);
        Assert.Single(inquiry.Build().ToList());
    }

    /// <summary>
    /// Inside a quantifier is still inside the query, so a backend without regular expressions does not get one
    /// smuggled past it in a collection
    /// </summary>
    [Fact]
    public void AnOperatorInsideAQuantifierIsRefusedToo()
    {
        var inquiry = BoundCrews(NoRegex).ApplyCondition("Heists Any (Target IsMatch 'Ban')");

        Assert.Equal(WeequeryError.NotTranslatable, Assert.Single(inquiry.Validate().Problems).Error);

        // while the same query without the regular expression is fine
        Assert.True(BoundCrews(NoRegex).ApplyCondition("Heists Any (Target = 'Bank')").Validate().IsValid);
    }

    /// <summary>
    /// A conjunction names no field, so the refusal says what it can rather than inventing one
    /// </summary>
    [Fact]
    public void AStructuralOperatorCanBeRefusedAndSaysSoWithoutAField()
    {
        var inquiry = Bound(OperatorSupport.Supporting(Operator.Equals)).ApplyCondition("Name = 'a' AND Name = 'b'");

        var problem = Assert.Single(inquiry.Validate().Problems);

        Assert.Equal(WeequeryError.NotTranslatable, problem.Error);
        Assert.Equal("test: And is not supported by this data source", problem.ToString());
    }

    /// <summary>
    /// Sorts and projections have no operators in them, so the strictest set possible leaves them alone
    /// </summary>
    [Fact]
    public void SortsAndProjectionsAreNotTouchedByIt()
    {
        var inquiry = Bound(OperatorSupport.Supporting())
            .ApplySorts("Pay DESC")
            .ApplyProjection("Name, Pay");

        Assert.True(inquiry.Validate().IsValid);
        Assert.Equal(4, inquiry.BuildProjected().ToList().Count);
    }

    /// <summary>
    /// One problem per half per pass, as everywhere else: a condition using two operators the backend lacks
    /// reports the first of them
    /// </summary>
    [Fact]
    public void TheFirstUnsupportedOperatorIsTheOneReported()
    {
        var problem = Assert.Single(
            Bound(NoRegex).ApplyCondition("Name IsMatch '^A' AND Alias DoesNotMatch 'z'").Validate().Problems);

        Assert.Contains("IsMatch", problem.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("DoesNotMatch", problem.Message, StringComparison.Ordinal);
    }
}
