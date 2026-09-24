using Tests.Common;
using Weequery;
using Weequery.Bindings;

namespace Tests.Unit;

/// <summary>
/// BindResolve keeps what it built under the arguments it was given, compared by what the settings hold
/// </summary>
public class ResolutionCacheTests
{
    /// <summary>Only these tests resolve it, so nothing else can have filled its cache first</summary>
    private sealed class Cached
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public CachedInner? Inner { get; set; }
    }

    private sealed class CachedInner
    {
        public int Size { get; set; }
    }

    private static BindingResolutionSettings Ignoring(params string[] paths)
    {
        return BindingResolutionSettings.Default with { IgnorePaths = [.. paths] };
    }

    // ---------- the key ----------

    [Fact]
    public void SettingsHoldingTheSameThingsAreOneKey()
    {
        var first = new ResolutionKey(1, Ignoring("Pay", "Lair."), BindingUse.All);
        var second = new ResolutionKey(1, Ignoring("lair.", "PAY"), BindingUse.All);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void EveryArgumentIsPartOfTheKey()
    {
        var key = new ResolutionKey(1, BindingResolutionSettings.Default, BindingUse.All);

        Assert.NotEqual(key, new ResolutionKey(2, BindingResolutionSettings.Default, BindingUse.All));
        Assert.NotEqual(key, new ResolutionKey(1, BindingResolutionSettings.Default, BindingUse.Projection));
        Assert.NotEqual(key, new ResolutionKey(1, Ignoring("Pay"), BindingUse.All));
        Assert.NotEqual(key, new ResolutionKey(1, BindingResolutionSettings.Default with { IgnoreTypes = [typeof(int)] }, BindingUse.All));
        Assert.NotEqual(key, new ResolutionKey(1, BindingResolutionSettings.Default with { DoNotExpandTypes = [typeof(Lair)] }, BindingUse.All));
        Assert.NotEqual(key, new ResolutionKey(1, BindingResolutionSettings.Default with { IgnoreTypeWhenAssignable = true }, BindingUse.All));
    }

    /// <summary>
    /// The sets are copied, so changing one after the call cannot change what an entry was stored under
    /// </summary>
    [Fact]
    public void ChangingTheSettingsAfterwardsDoesNotChangeTheKey()
    {
        var settings = Ignoring("Pay");
        var key = new ResolutionKey(1, settings, BindingUse.All);

        settings.IgnorePaths.Add("Lair.");

        Assert.NotEqual(key, new ResolutionKey(1, settings, BindingUse.All));
        Assert.Equal(key, new ResolutionKey(1, Ignoring("Pay"), BindingUse.All));
    }

    // ---------- the cache ----------

    [Fact]
    public void EqualSettingsBuildOnce()
    {
        var built = 0;
        ResolvedBindingSet<Cached> Resolve()
        {
            built++;
            return new(new(), []);
        }

        var first = BindingSetCache<Cached>.ForResolution(0, Ignoring("Inner.Size"), BindingUse.Test, Resolve);
        var second = BindingSetCache<Cached>.ForResolution(0, Ignoring("INNER.SIZE"), BindingUse.Test, Resolve);

        Assert.Equal(1, built);
        Assert.Same(first, second);
    }

    // ---------- through BindResolve ----------

    [Fact]
    public void FreshSettingsPerCallBindTheSame()
    {
        var first = MinionTestData.Minions().WithWeequery().BindResolve(settings: Ignoring("Pay")).ListBindings();
        var second = MinionTestData.Minions().WithWeequery().BindResolve(settings: Ignoring("pay")).ListBindings();

        Assert.Equal(first.Select(bound => bound.Key), second.Select(bound => bound.Key));
        Assert.DoesNotContain(first, bound => bound.Key == "Pay");
    }

    /// <summary>
    /// The kept set is shared, so what one Inquiry does to its own copy cannot reach the next one resolved
    /// </summary>
    [Fact]
    public void RemovingFromOneResolvedInquiryLeavesTheNextWhole()
    {
        var settings = Ignoring("Name");

        var first = new List<Cached>().AsQueryable().WithWeequery().BindResolve(settings: settings).RemoveBinding("Inner.Size");
        var second = new List<Cached>().AsQueryable().WithWeequery().BindResolve(settings: settings);

        Assert.DoesNotContain(first.ListBindings(), bound => bound.Key == "Inner.Size");
        Assert.Contains(second.ListBindings(), bound => bound.Key == "Inner.Size");
    }

    [Fact]
    public void ARepeatedResolveStillQueries()
    {
        var settings = Ignoring("Pay");

        MinionTestData.Minions().WithWeequery().BindResolve(settings: settings);
        var matched = MinionTestData.Minions().WithWeequery().BindResolve(settings: settings)
            .ApplyCondition("IsActive = true And LairAssignments None (Lair.Name = 'Volcano')").Build().ToList();

        Assert.Equal(["Alice Fox", "Bob Samuelson", "David Edgars"], matched.Select(minion => minion.Name));
    }
}
