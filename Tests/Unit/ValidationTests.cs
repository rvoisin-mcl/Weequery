using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// Validate answers what Build would refuse, without building and without throwing. See
/// <see cref="Inquiry{T}.Validate()"/>.
/// <para>
/// The two things worth pinning are that it reports <i>every</i> half that is wrong rather than the first, since
/// a caller fixing one fault per round trip is the thing it exists to prevent, and that asking changes nothing:
/// the query built afterwards is the query that would have been built without asking.
/// </para>
/// </summary>
public class ValidationTests
{
    private static Inquiry<Minion> Bound()
    {
        return MinionTestData.Minions().WithWeequery().BindProperties(Minion.Bindings);
    }

    private static ValidationProblem Only(ValidationResult result)
    {
        return Assert.Single(result.Problems);
    }

    // ---------- nothing wrong ----------

    [Fact]
    public void AQueryWithNothingAppliedIsValid()
    {
        Assert.True(Bound().Validate().IsValid);
    }

    [Fact]
    public void AQueryThatWillBuildIsValid()
    {
        var result = Bound()
            .ApplyCondition("Pay > 10000 AND Name StartsWith 'A'")
            .ApplySorts("Pay DESC, Name")
            .ApplyProjection("Name, Pay")
            .Validate();

        Assert.True(result.IsValid);
        Assert.Empty(result.Problems);
    }

    /// <summary>
    /// A projection nobody asked for reads every projectable binding, so there is nothing of the caller's to be
    /// wrong about
    /// </summary>
    [Fact]
    public void NoProjectionIsNotAProblem()
    {
        Assert.True(Bound().ApplyCondition("IsActive = true").Validate().IsValid);
    }

    // ---------- each half, refused ----------

    [Fact]
    public void AConditionNamingAnUnboundFieldIsReportedAgainstTheCondition()
    {
        var problem = Only(Bound().ApplyCondition("Gizmo = 3").Validate());

        Assert.Equal(BindingUse.Test, problem.Part);
        Assert.Equal(WeequeryError.UnboundField, problem.Error);
        Assert.Contains("Gizmo", problem.Message);
    }

    [Fact]
    public void ASortNamingAnUnboundFieldIsReportedAgainstTheSort()
    {
        var problem = Only(Bound().ApplySorts("Gizmo DESC").Validate());

        Assert.Equal(BindingUse.Sort, problem.Part);
        Assert.Contains("Gizmo", problem.Message);
    }

    [Fact]
    public void AProjectionNamingAnUnboundFieldIsReportedAgainstTheProjection()
    {
        var problem = Only(Bound().ApplyProjection("Gizmo").Validate());

        Assert.Equal(BindingUse.Projection, problem.Part);
        Assert.Contains("Gizmo", problem.Message);
    }

    /// <summary>
    /// Not every refusal is an unbound field: an operator the property cannot take is the other common one
    /// </summary>
    [Fact]
    public void AnOperatorTheTypeDoesNotTakeIsReported()
    {
        var problem = Only(Bound().ApplyCondition("Pay StartsWith '1'").Validate());

        Assert.Equal(BindingUse.Test, problem.Part);
        Assert.False(problem.Error == WeequeryError.Unspecified);
    }

    [Fact]
    public void ASortOnSomethingWithNoOrderingIsDroppedRatherThanReported()
    {
        // No longer a problem because it is no longer refused: a sort with nothing to order by is dropped, and
        // Validate fills DroppedFields exactly as a build does, so that is where it shows up
        var inquiry = Bound().BindConstant("Threshold", 10000m).ApplySorts("Threshold");

        Assert.True(inquiry.Validate().IsValid);

        var dropped = Assert.Single(inquiry.DroppedFields);

        Assert.Equal("Threshold", dropped.Field);
        Assert.Equal(BindingUse.Sort, dropped.From);
    }

    // ---------- all of it at once, which is the point ----------

    /// <summary>
    /// The whole reason this returns a list. Three faults, one call, three findings, one per half
    /// </summary>
    [Fact]
    public void EveryHalfIsLookedAtWhateverTheOnesBeforeItSaid()
    {
        var result = Bound()
            .ApplyCondition("Gizmo = 3")
            .ApplySorts("Doohickey DESC")
            .ApplyProjection("Widget")
            .Validate();

        Assert.False(result.IsValid);
        Assert.Equal([BindingUse.Test, BindingUse.Sort, BindingUse.Projection], result.Problems.Select(problem => problem.Part));
    }

    // ---------- and asking costs nothing ----------

    /// <summary>
    /// Nothing is executed and nothing is kept, so the query built after asking is the query that would have
    /// been built without asking
    /// </summary>
    [Fact]
    public void ValidatingDoesNotChangeWhatIsBuilt()
    {
        var inquiry = Bound().ApplyCondition("Pay > 10000").ApplySorts("Pay DESC");

        Assert.True(inquiry.Validate().IsValid);
        Assert.True(inquiry.Validate().IsValid);

        Assert.Equal(["Charlie Smith", "Alice Fox"], inquiry.Build().Select(minion => minion.Name).ToList());
    }

    /// <summary>
    /// Validate says it will build; the build then says the same thing by not throwing, and an invalid one says
    /// it by throwing the refusal that was reported
    /// </summary>
    [Fact]
    public void WhatValidateReportsIsWhatBuildThrows()
    {
        var inquiry = Bound().ApplyCondition("Gizmo = 3");

        var problem = Only(inquiry.Validate());
        var thrown = Assert.Throws<WeequeryException>(() => inquiry.Build());

        Assert.Equal(thrown.Error, problem.Error);
        Assert.Equal(thrown.Message, problem.Message);
    }

    /// <summary>
    /// The one thing it does leave behind, since what a query quietly drops is worth knowing at the same time as
    /// what it refuses outright
    /// </summary>
    [Fact]
    public void ValidateFillsDroppedFieldsAsABuildWould()
    {
        var inquiry = Bound().IgnoreUnboundFields().ApplyCondition("IsActive = true AND Gizmo = 3");

        Assert.True(inquiry.Validate().IsValid);

        var dropped = Assert.Single(inquiry.DroppedFields);
        Assert.Equal("Gizmo", dropped.Field);
        Assert.Equal(BindingUse.Test, dropped.From);
    }

    /// <summary>
    /// A field bound for something else is a deliberate statement about what a caller may do, so it is refused
    /// rather than dropped, lenient or not
    /// </summary>
    [Fact]
    public void ABindingThatDoesNotGrantTheUseIsStillReportedWhenUnboundFieldsAreIgnored()
    {
        var problem = Only(MinionTestData.Minions()
            .WithWeequery()
            .BindProperty(minion => minion.Name, "Name", BindingUse.Projection)
            .IgnoreUnboundFields()
            .ApplyCondition("Name = 'Alice Fox'")
            .Validate());

        Assert.Equal(BindingUse.Test, problem.Part);
    }

    // ---------- the result itself ----------

    [Fact]
    public void TheValidResultIsShared()
    {
        Assert.Same(ValidationResult.Valid, Bound().Validate());
        Assert.True(ValidationResult.Valid.IsValid);
    }

    [Fact]
    public void AProblemReadsAsWhereAndWhat()
    {
        var problem = new ValidationProblem(BindingUse.Sort, WeequeryError.UnboundField, "'Gizmo' is not bound");

        Assert.Equal("sort: 'Gizmo' is not bound", problem.ToString());
        Assert.Equal("'Gizmo' is not bound", new ValidationProblem(BindingUse.None, WeequeryError.ArgumentInvalid, "'Gizmo' is not bound").ToString());
    }
}
