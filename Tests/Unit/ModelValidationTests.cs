using Microsoft.EntityFrameworkCore;
using Tests.Common;
using Weequery;
using Weequery.EntityFrameworkCore;

namespace Tests.Unit;

/// <summary>
/// Checking a binding list against the EF model, which is the half <see cref="Inquiry{T}.Validate()"/> cannot
/// answer.
/// <para>
/// Weequery has no opinion about what a database holds, so a binding to a path nothing maps passes every check
/// the library makes and fails on the first request that names it. These are about moving that failure to
/// startup, where it belongs to whoever wrote the bindings rather than to whoever sent the query.
/// </para>
/// </summary>
public class ModelValidationTests
{
    private static DBContext Context()
    {
        // Nothing is executed, so the context needs no database behind it
        return new DBContext(new());
    }

    // ---------- what the model accounts for ----------

    [Fact]
    public void TheRealBindingsAllMap()
    {
        using var context = Context();

        var result = context.ValidateBindings<Minion>(Minion.Bindings);

        Assert.True(result.IsValid, result.ToString());
    }

    [Fact]
    public void ANestedPathThroughAReferenceMaps()
    {
        using var context = Context();

        // LairAssignment holds the reference navigation, which is the only way to reach a Lair's own columns
        var result = context.ValidateBindings<LairAssignment>([new BindingRequest("Lair.Name", "LairName")]);

        Assert.True(result.IsValid, result.ToString());
    }

    /// <summary>Binding the navigation itself is legal, and only the null tests will work on it</summary>
    [Fact]
    public void ANavigationOnItsOwnMaps()
    {
        using var context = Context();

        Assert.True(context.ValidateBindings<LairAssignment>([new BindingRequest("Lair", null)]).IsValid);
    }

    // ---------- what it does not ----------

    [Fact]
    public void APathNothingMapsIsReported()
    {
        using var context = Context();

        var result = context.ValidateBindings<Minion>([new BindingRequest("Gizmo", null)]);

        var problem = Assert.Single(result.Problems);

        Assert.Equal(WeequeryError.NotTranslatable, problem.Error);
        Assert.Contains("Gizmo", problem.Message);
        Assert.Contains(nameof(Minion), problem.Message);
    }

    /// <summary>The message names the level that failed, not just the whole path</summary>
    [Fact]
    public void AMisspellingPartWayDownNamesWhereItLooked()
    {
        using var context = Context();

        var result = context.ValidateBindings<LairAssignment>([new BindingRequest("Lair.Capasity", "Capacity")]);

        var problem = Assert.Single(result.Problems);

        Assert.Contains("Capasity", problem.Message);

        // Lair rather than LairAssignment: the walk got through the navigation and failed inside it
        Assert.Contains($"{nameof(Lair)} has no", problem.Message);
    }

    /// <summary>A key that is not the path is named as well, since that is what a caller sends</summary>
    [Fact]
    public void BothTheKeyAndThePathAreNamed()
    {
        using var context = Context();

        var result = context.ValidateBindings<Minion>([new BindingRequest("Gizmo", "Widget")]);

        Assert.Contains("Widget", result.Problems[0].Message);
        Assert.Contains("Gizmo", result.Problems[0].Message);
    }

    [Fact]
    public void ReachingThroughAColumnSaysSoRatherThanSayingUnmapped()
    {
        using var context = Context();

        var result = context.ValidateBindings<Minion>([new BindingRequest("Name.Length", "NameLength")]);

        var problem = Assert.Single(result.Problems);

        Assert.Contains("column", problem.Message);
        Assert.Contains("Length", problem.Message);
    }

    [Fact]
    public void ReachingThroughACollectionSaysToUseAQuantifier()
    {
        using var context = Context();

        var result = context.ValidateBindings<Minion>([new BindingRequest("LairAssignments.LairID", "AssignedLair")]);

        var problem = Assert.Single(result.Problems);

        Assert.Contains("collection", problem.Message);
        Assert.Contains("quantifier", problem.Message);
    }

    [Fact]
    public void AnEntityOutsideTheModelIsOneProblemRatherThanOnePerBinding()
    {
        using var context = Context();

        var result = context.ValidateBindings<Crew>([new BindingRequest("Name", null), new BindingRequest("Id", null)]);

        var problem = Assert.Single(result.Problems);

        Assert.Contains(nameof(Crew), problem.Message);
    }

    // ---------- the shape of the answer ----------

    /// <summary>Every binding is looked at, so one call lists all of them rather than the first</summary>
    [Fact]
    public void EveryBadBindingIsReported()
    {
        using var context = Context();

        var result = context.ValidateBindings<Minion>(
        [
            new BindingRequest(nameof(Minion.Name), null),
            new BindingRequest("Gizmo", null),
            new BindingRequest(nameof(Minion.Pay), null),
            new BindingRequest("Widget", null),
        ]);

        Assert.Equal(2, result.Problems.Count);
        Assert.Contains("Gizmo", result.Problems[0].Message);
        Assert.Contains("Widget", result.Problems[1].Message);
    }

    [Fact]
    public void TheArgumentsAreChecked()
    {
        using var context = Context();

        Assert.Throws<WeequeryException>(() => ((DbContext)null!).ValidateBindings<Minion>(Minion.Bindings));
        Assert.Throws<WeequeryException>(() => context.ValidateBindings<Minion>(null!));
    }

    /// <summary>The point of it: no connection, so it can run where there is no database yet</summary>
    [Fact]
    public void NothingIsExecuted()
    {
        using var context = Context();

        context.ValidateBindings<Minion>(Minion.Bindings);

        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
    }
}
