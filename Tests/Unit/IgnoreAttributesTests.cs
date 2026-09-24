using System.ComponentModel.DataAnnotations.Schema;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// BindingResolutionSettings.IgnoreAttributes: a property, or a type, carrying a listed attribute is not bound
/// </summary>
public class IgnoreAttributesTests
{
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Class, Inherited = true)]
    private class SecretAttribute : Attribute { }

    /// <summary>Derived from one that is listed, so counts as it</summary>
    private sealed class TopSecretAttribute : SecretAttribute { }

    private sealed class Account
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";

        [NotMapped]
        public string Display { get; set; } = "";

        [NotMapped]
        public Address? Computed { get; set; }

        public Address? Home { get; set; }

        public Vault? Vault { get; set; }

        [TopSecret]
        public int Pin { get; set; }

        public List<Vault>? Vaults { get; set; }

        [NotMapped]
        public List<Address>? History { get; set; }

        public List<Address>? Addresses { get; set; }
    }

    private sealed class Address
    {
        public string Street { get; set; } = "";

        [NotMapped]
        public string Formatted { get; set; } = "";
    }

    /// <summary>The attribute is on the type, so every property of this type is left out</summary>
    [Secret]
    private sealed class Vault
    {
        public int Code { get; set; }
    }

    private class BaseEntity
    {
        [NotMapped]
        public virtual int Cached { get; set; }
    }

    private sealed class DerivedEntity : BaseEntity
    {
        public int Id { get; set; }

        public override int Cached { get; set; }
    }

    private static readonly BindingResolutionSettings NotMapped = BindingResolutionSettings.Default with { IgnoreAttributes = [typeof(NotMappedAttribute)] };

    private static readonly BindingResolutionSettings Both = BindingResolutionSettings.Default with { IgnoreAttributes = [typeof(NotMappedAttribute), typeof(SecretAttribute)] };

    private static List<string> Keys(BindingResolutionSettings? settings, int maxDepth = 1)
    {
        return [.. Inquiry<Account>.ResolveBindables(maxDepth, settings).Select(request => request.Key)];
    }

    [Fact]
    public void NotMappedIsIgnoredByDefault()
    {
        Assert.Equal([typeof(NotMappedAttribute)], BindingResolutionSettings.Default.IgnoreAttributes);

        Assert.DoesNotContain("Display", Keys(null));
        Assert.DoesNotContain("Display", Keys(BindingResolutionSettings.Default));
        Assert.DoesNotContain("Display", new List<Account>().AsQueryable().WithWeequery().BindResolve().ListBindings().Select(entry => entry.Key));
    }

    /// <summary>
    /// The record built by hand has an empty set, the same as it gives up every other default
    /// </summary>
    [Fact]
    public void SettingsBuiltByHandIgnoreNoAttributes()
    {
        var settings = new BindingResolutionSettings([], [], false, []);

        Assert.Empty(settings.IgnoreAttributes);
        Assert.Contains("Display", Keys(settings));
    }

    /// <summary>
    /// Assigning a set replaces the default one, and an empty one turns it off
    /// </summary>
    [Fact]
    public void AssigningASetReplacesTheDefault()
    {
        Assert.Contains("Display", Keys(BindingResolutionSettings.Default with { IgnoreAttributes = [] }));

        var secret = BindingResolutionSettings.Default with { IgnoreAttributes = [typeof(SecretAttribute)] };
        Assert.Contains("Display", Keys(secret));
        Assert.DoesNotContain("Pin", Keys(secret));

        var both = BindingResolutionSettings.Default with { IgnoreAttributes = [.. BindingResolutionSettings.Default.IgnoreAttributes, typeof(SecretAttribute)] };
        Assert.DoesNotContain("Display", Keys(both));
        Assert.DoesNotContain("Pin", Keys(both));
    }

    [Fact]
    public void APropertyCarryingTheAttributeIsNotBound()
    {
        var keys = Keys(NotMapped);

        Assert.DoesNotContain("Display", keys);
        Assert.Contains("Name", keys);
        Assert.Contains("Id", keys);
    }

    [Fact]
    public void NothingBelowItIsBoundEither()
    {
        var keys = Keys(NotMapped);

        Assert.DoesNotContain("Computed", keys);
        Assert.DoesNotContain(keys, key => key.StartsWith("Computed.", StringComparison.Ordinal));
    }

    [Fact]
    public void ItAppliesAtEveryDepth()
    {
        var keys = Keys(NotMapped);

        Assert.Contains("Home.Street", keys);
        Assert.DoesNotContain("Home.Formatted", keys);
    }

    [Fact]
    public void APropertyWhoseTypeCarriesTheAttributeIsNotBound()
    {
        var keys = Keys(Both);

        Assert.DoesNotContain("Vault", keys);
        Assert.DoesNotContain("Vault.Code", keys);
    }

    [Fact]
    public void ADerivedAttributeCountsAsTheOneListed()
    {
        Assert.Contains("Pin", Keys(NotMapped));
        Assert.DoesNotContain("Pin", Keys(Both));
    }

    [Fact]
    public void AnAttributeOnAnOverriddenPropertyIsInherited()
    {
        var keys = Inquiry<DerivedEntity>.ResolveBindables(0, NotMapped).Select(request => request.Key).ToList();

        Assert.Equal(["Id"], keys);
    }

    [Fact]
    public void ACollectionCarryingTheAttributeIsNeitherPropertyNorCollection()
    {
        Assert.DoesNotContain("History", Keys(NotMapped));
        Assert.DoesNotContain(Inquiry<Account>.ResolveBindableCollections(1, NotMapped), found => found.Key == "History");
    }

    /// <summary>
    /// The element type is what carries it, so there is nothing to quantify over, and the list itself is still
    /// there to be null tested, the same as an ignored element type
    /// </summary>
    [Fact]
    public void ACollectionWhoseElementCarriesTheAttributeIsNotQuantified()
    {
        Assert.Contains("Vaults", Keys(Both));
        Assert.DoesNotContain(Inquiry<Account>.ResolveBindableCollections(1, Both), found => found.Key == "Vaults");
    }

    [Fact]
    public void ItAppliesInsideAnElement()
    {
        var addresses = Assert.Single(Inquiry<Account>.ResolveBindableCollections(1, NotMapped), found => found.Key == "Addresses");

        Assert.Equal(["Street"], addresses.Elements.Select(request => request.Key));
    }

    /// <summary>
    /// A path aimed inside a collection has the settings copied for the element, and the copy keeps the attributes
    /// </summary>
    [Fact]
    public void ScopingThePathsToAnElementKeepsTheAttributes()
    {
        var settings = NotMapped with { IgnorePaths = ["Addresses[].Nothing"] };

        var addresses = Assert.Single(Inquiry<Account>.ResolveBindableCollections(1, settings), found => found.Key == "Addresses");

        Assert.Equal(["Street"], addresses.Elements.Select(request => request.Key));
    }

    [Fact]
    public void TheCopyConstructorKeepsThem()
    {
        Assert.Equal(NotMapped.IgnoreAttributes, new BindingResolutionSettings(NotMapped).IgnoreAttributes);
    }

    [Fact]
    public void BindResolveLeavesThemOut()
    {
        var bound = new List<Account>().AsQueryable().WithWeequery().BindResolve(settings: NotMapped).ListBindings();

        Assert.DoesNotContain(bound, entry => entry.Key == "Display");
        Assert.DoesNotContain(bound, entry => entry.Key == "History");
        Assert.Contains(bound, entry => entry.Key == "Name");
    }

    /// <summary>
    /// The attributes are part of the cache key, so asking without them after asking with them is not served the
    /// narrower set
    /// </summary>
    [Fact]
    public void BindResolveIsCachedPerAttributeSet()
    {
        var without = new List<Account>().AsQueryable().WithWeequery().BindResolve(settings: NotMapped);
        var with = new List<Account>().AsQueryable().WithWeequery().BindResolve(settings: BindingResolutionSettings.Default with { IgnoreAttributes = [] });

        Assert.DoesNotContain(without.ListBindings(), entry => entry.Key == "Display");
        Assert.Contains(with.ListBindings(), entry => entry.Key == "Display");
    }

    [Fact]
    public void ATypeThatIsNotAnAttributeIsRefused()
    {
        var settings = BindingResolutionSettings.Default with { IgnoreAttributes = [typeof(string)] };

        var thrown = Assert.Throws<WeequeryException>(() => Inquiry<Account>.ResolveBindables(1, settings));
        Assert.Equal(WeequeryError.ArgumentInvalid, thrown.Error);

        Assert.Throws<WeequeryException>(() => Inquiry<Account>.ResolveBindableCollections(1, settings));
        Assert.Throws<WeequeryException>(() => new List<Account>().AsQueryable().WithWeequery().BindResolve(settings: settings));
    }
}
