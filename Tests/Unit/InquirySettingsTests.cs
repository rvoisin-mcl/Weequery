using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// The rules a query picks for itself, see <see cref="InquirySettings"/>. Every string comparison follows them
/// where Weequery is the one comparing, which is in memory.
/// </summary>
public class InquirySettingsTests
{
    /// <summary>U+00AD, ignorable to a linguistic comparison and not to an ordinal one</summary>
    private static readonly string SoftHyphen = ((char)0x00AD).ToString();

    /// <summary>The rules a query has to ask for, the default being ordinal</summary>
    private static readonly InquirySettings Linguistic = InquirySettings.Default with { StringComparison = StringComparison.CurrentCulture };

    private static IQueryable<Minion> Named(params string[] names)
    {
        return names.Select(name => new Minion { MinionID = Guid.NewGuid(), Name = name }).AsQueryable();
    }

    private static string[] Matching(IQueryable<Minion> minions, string query, InquirySettings? settings = null)
    {
        return [.. minions
            .WithWeequery(settings)
            .BindProperties(Minion.Bindings)
            .ApplyCondition(query)
            .Build()
            .Select(minion => minion.Name)];
    }

    // ---------- the default ----------

    /// <summary>
    /// Ordinal, which is what a database does, so the two evaluation paths agree unless a query asks them not to
    /// </summary>
    [Fact]
    public void TheDefaultIsOrdinal()
    {
        Assert.Equal(StringComparison.Ordinal, InquirySettings.Default.StringComparison);
        Assert.Equal(StringComparison.Ordinal, new InquirySettings().StringComparison);
    }

    [Fact]
    public void AQueryGivenNoSettingsTakesTheDefault()
    {
        Assert.Same(InquirySettings.Default, Named("Acme").WithWeequery().Settings);
    }

    // ---------- and what picking another one does ----------

    /// <summary>
    /// The case the setting exists for: the same condition over the same row, answered two ways because the two
    /// queries asked for different rules
    /// </summary>
    [Fact]
    public void OneQueryCanCompareOrdinallyAndAnotherLinguistically()
    {
        var minions = Named("Acme");
        var query = $"Name Contains 'Ac{SoftHyphen}me'";

        Assert.Empty(Matching(minions, query));
        Assert.Equal(["Acme"], Matching(minions, query, Linguistic));
        Assert.Equal(["Acme"], Matching(minions, query, InquirySettings.Default with { StringComparison = StringComparison.InvariantCulture }));
    }

    /// <summary>
    /// A case-insensitive rule reaches the substring operators as readily as an ignorable character does, which
    /// is the other thing a caller is likely to want from this
    /// </summary>
    [Fact]
    public void ACaseInsensitiveRuleAppliesToTheSubstringOperators()
    {
        var minions = Named("Acme");

        Assert.Empty(Matching(minions, "Name StartsWith 'acme'"));
        Assert.Equal(["Acme"], Matching(minions, "Name StartsWith 'acme'", InquirySettings.Default with { StringComparison = StringComparison.OrdinalIgnoreCase }));
    }

    /// <summary>
    /// Ordering is a comparison too, and string.Compare takes the same rules. Ordinal puts every upper case
    /// letter before every lower case one, which a linguistic comparison does not.
    /// </summary>
    [Fact]
    public void TheOrderingComparisonsFollowTheRulesToo()
    {
        var minions = Named("a", "B");

        // ordinally 'B' (0x42) is below 'a' (0x61), so nothing is greater than 'a'
        Assert.Empty(Matching(minions, "Name > 'a'"));

        // linguistically 'a' sorts before 'B', so 'B' is the one that is greater
        Assert.Equal(["B"], Matching(minions, "Name > 'a'", Linguistic));
    }

    /// <summary>
    /// Equality routes through the setting too, so the rules decide whether an ignorable character is a
    /// difference. See StringMatchingSemanticsTests for what that means against a database.
    /// </summary>
    [Fact]
    public void EqualityIsGovernedByTheSetting()
    {
        var minions = Named($"Ac{SoftHyphen}me");

        Assert.Empty(Matching(minions, "Name = 'Acme'"));
        Assert.Single(Matching(minions, "Name = 'Acme'", Linguistic));
        Assert.Single(Matching(minions, "Name = 'Acme'", InquirySettings.Default with { StringComparison = StringComparison.InvariantCulture }));
    }

    /// <summary>
    /// And its negative, which is the same comparison read the other way round
    /// </summary>
    [Fact]
    public void InequalityFollowsEquality()
    {
        var minions = Named($"Ac{SoftHyphen}me");

        Assert.Single(Matching(minions, "Name <> 'Acme'"));
        Assert.Empty(Matching(minions, "Name <> 'Acme'", Linguistic));
    }

    /// <summary>
    /// The IsIn family is built from a list rather than from a chain of equality tests, so it needs the rules
    /// putting on it separately. It has to agree with the equality it stands in for.
    /// </summary>
    [Fact]
    public void TheIsInFamilyIsGovernedByTheSetting()
    {
        var minions = Named($"Ac{SoftHyphen}me");

        Assert.Empty(Matching(minions, "Name IsIn ('Acme', 'Other')"));
        Assert.Single(Matching(minions, "Name IsIn ('Acme', 'Other')", Linguistic));

        Assert.Single(Matching(minions, "Name IsNotIn ('Acme', 'Other')"));
        Assert.Empty(Matching(minions, "Name IsNotIn ('Acme', 'Other')", Linguistic));
    }

    /// <summary>
    /// A range is built from the ordering comparisons, so it takes the rules with them
    /// </summary>
    [Fact]
    public void ARangeIsGovernedByTheSetting()
    {
        var minions = Named("B");

        // ordinally 'B' (0x42) is below 'a' (0x61), so it falls outside the range
        Assert.Empty(Matching(minions, "Name IsBetween ('a', 'c')"));

        // linguistically 'a' sorts before 'B', so 'B' falls inside it
        Assert.Single(Matching(minions, "Name IsBetween ('a', 'c')", Linguistic));
    }

    /// <summary>
    /// A comparison against another bound property is built by a different path, and has to take the rules the
    /// same way a comparison against a value does
    /// </summary>
    [Fact]
    public void ComparingAgainstAnotherPropertyIsGovernedByTheSetting()
    {
        var minions = new[] { new Minion { MinionID = Guid.NewGuid(), Name = "Acme", Alias = $"Ac{SoftHyphen}me" } }.AsQueryable();

        static string[] Run(IQueryable<Minion> minions, string query, InquirySettings? settings)
        {
            return [.. minions
                .WithWeequery(settings)
                .BindProperty(minion => minion.Name)
                .BindProperty(minion => minion.Alias)
                .ApplyCondition(query)
                .Build()
                .Select(minion => minion.Name)];
        }

        Assert.Empty(Run(minions, "Name = [Alias]", null));
        Assert.Single(Run(minions, "Name = [Alias]", Linguistic));

        Assert.Empty(Run(minions, "Name StartsWith [Alias]", null));
        Assert.Single(Run(minions, "Name StartsWith [Alias]", Linguistic));
    }

    /// <summary>
    /// The null tests ask whether the value is there rather than what it is, so no rule applies to them and the
    /// guard a nullable binding carries is left as it was
    /// </summary>
    [Fact]
    public void TheNullTestsAreUnaffected()
    {
        var minions = new[]
        {
            new Minion { MinionID = Guid.NewGuid(), Name = "Acme", Alias = null },
            new Minion { MinionID = Guid.NewGuid(), Name = "Other", Alias = "Ghost" },
        }.AsQueryable();

        foreach (var comparison in new[] { StringComparison.Ordinal, StringComparison.CurrentCulture })
        {
            var settings = InquirySettings.Default with { StringComparison = comparison };

            Assert.Equal(["Acme"], Matching(minions, "Alias IsNull", settings));
            Assert.Equal(["Other"], Matching(minions, "Alias IsNotNull", settings));

            // and a null still matches nothing, rather than matching another null
            Assert.Equal(["Other"], Matching(minions, "Alias = 'Ghost'", settings));
        }
    }

    // ---------- where it reaches ----------

    [Fact]
    public void CloningCarriesTheSettings()
    {
        var inquiry = Named("Acme").WithWeequery(Linguistic).BindProperties(Minion.Bindings);

        Assert.Same(Linguistic, inquiry.Clone().Settings);
        Assert.Single(inquiry.Clone().ApplyCondition($"Name Contains 'Ac{SoftHyphen}me'").Build().ToList());
    }

    [Fact]
    public void BuildDelegateTakesThemToo()
    {
        var condition = new OneValueCondition<string>(Operator.Contains, nameof(Minion.Name), $"Ac{SoftHyphen}me");
        var minion = new Minion { MinionID = Guid.NewGuid(), Name = "Acme" };

        Assert.False(Inquiry<Minion>.BuildDelegate(Minion.Bindings, condition)(minion));
        Assert.True(Inquiry<Minion>.BuildDelegate(Minion.Bindings, condition, Linguistic)(minion));
    }

    /// <summary>
    /// The expression a caller takes for itself is the translatable one, so the rules are not compiled into it:
    /// the forms carrying a StringComparison are not ones a provider reads. Whoever takes it decides.
    /// </summary>
    [Fact]
    public void TheTranslatableExpressionIsLeftAlone()
    {
        var condition = new OneValueCondition<string>(Operator.Contains, nameof(Minion.Name), $"Ac{SoftHyphen}me");

        var expression = Inquiry<Minion>.BuildExpression(Minion.Bindings, condition);

        Assert.DoesNotContain(nameof(StringComparison), expression.ToString(), StringComparison.Ordinal);
    }
}
