using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// Characterization tests for the string operators. These record what Weequery does today, they are not a
/// statement that the behaviour is desirable.
/// <para>
/// A translated query takes its rules from the column collation. In memory the query says, see
/// <see cref="InquirySettings.StringComparison"/>, and it is <see cref="StringComparison.Ordinal"/> unless the
/// query asked for something else. Ordinal is what a database does, so the default is the setting under which
/// the two evaluation paths agree; asking for a culture is what makes them diverge.
/// </para>
/// <para>
/// If one of these fails, the comparison semantics changed. That may well be an improvement, but it is a
/// behavioural break for anyone relying on the current rules, so it should be a deliberate decision rather than a
/// side effect.
/// </para>
/// </summary>
public class StringMatchingSemanticsTests
{
    /// <summary>
    /// U+00AD. Linguistic comparison treats it as ignorable, ordinal comparison does not, which makes it a clean
    /// way to tell the two apart without depending on any particular culture being installed.
    /// </summary>
    private static readonly string SoftHyphen = ((char)0x00AD).ToString();

    /// <summary>What a query has to ask for to get linguistic comparison</summary>
    private static readonly InquirySettings Linguistic = InquirySettings.Default with { StringComparison = StringComparison.CurrentCulture };

    private static IQueryable<Minion> Named(string name)
    {
        return new List<Minion> { new() { MinionID = Guid.NewGuid(), Name = name } }.AsQueryable();
    }

    private static int MatchesInMemory(IQueryable<Minion> minions, Operator op, string value, InquirySettings? settings = null)
    {
        return minions
            .WithWeequery(settings)
            .BindProperties(Minion.Bindings)
            .ApplyCondition(new OneValueCondition<string>(op, nameof(Minion.Name), value))
            .Build()
            .Count();
    }

    // ---------- in memory, by default: ordinal, so the stored characters are what is compared ----------

    [Fact]
    public void StartsWithDoesNotIgnoreAnIgnorableCharacterInMemory()
    {
        // "Acme" does not start with "Ac<SHY>me" ordinally, the soft hyphen having to be there literally
        Assert.Equal(0, MatchesInMemory(Named("Acme"), Operator.StartsWith, $"Ac{SoftHyphen}me"));
    }

    [Fact]
    public void EndsWithDoesNotIgnoreAnIgnorableCharacterInMemory()
    {
        Assert.Equal(0, MatchesInMemory(Named("Acme"), Operator.EndsWith, $"Ac{SoftHyphen}me"));
    }

    [Fact]
    public void ContainsDoesNotIgnoreAnIgnorableCharacterInMemory()
    {
        Assert.Equal(0, MatchesInMemory(Named("Acme"), Operator.Contains, $"Ac{SoftHyphen}me"));
    }

    /// <summary>
    /// The three used to disagree: StartsWith and EndsWith compared linguistically while Contains was ordinal, so
    /// for one value and one filter StartsWith matched and Contains did not, which cannot both be right. They now
    /// take one set of rules and answer alike, whichever rules the query picked.
    /// </summary>
    [Fact]
    public void TheSubstringOperatorsAgreeOnTheSameValueAndFilter()
    {
        var minions = Named("Acme");
        var filter = $"Ac{SoftHyphen}me";

        Assert.Equal(0, MatchesInMemory(minions, Operator.StartsWith, filter));
        Assert.Equal(0, MatchesInMemory(minions, Operator.EndsWith, filter));
        Assert.Equal(0, MatchesInMemory(minions, Operator.Contains, filter));

        Assert.Equal(1, MatchesInMemory(minions, Operator.StartsWith, filter, Linguistic));
        Assert.Equal(1, MatchesInMemory(minions, Operator.EndsWith, filter, Linguistic));
        Assert.Equal(1, MatchesInMemory(minions, Operator.Contains, filter, Linguistic));
    }

    [Fact]
    public void NegatedFormsFollowTheirPositiveCounterparts()
    {
        var minions = Named("Acme");
        var filter = $"Ac{SoftHyphen}me";

        Assert.Equal(1, MatchesInMemory(minions, Operator.DoesNotStartWith, filter));
        Assert.Equal(1, MatchesInMemory(minions, Operator.DoesNotEndWith, filter));
        Assert.Equal(1, MatchesInMemory(minions, Operator.DoesNotContain, filter));

        Assert.Equal(0, MatchesInMemory(minions, Operator.DoesNotStartWith, filter, Linguistic));
        Assert.Equal(0, MatchesInMemory(minions, Operator.DoesNotEndWith, filter, Linguistic));
        Assert.Equal(0, MatchesInMemory(minions, Operator.DoesNotContain, filter, Linguistic));
    }

    [Fact]
    public void PlainAsciiMatchingIsUnaffected()
    {
        // The divergence needs an ignorable character to show up, ordinary filters behave as expected
        var minions = Named("Acme Corp");

        Assert.Equal(1, MatchesInMemory(minions, Operator.StartsWith, "Acme"));
        Assert.Equal(1, MatchesInMemory(minions, Operator.EndsWith, "Corp"));
        Assert.Equal(1, MatchesInMemory(minions, Operator.Contains, "me Co"));
        Assert.Equal(0, MatchesInMemory(minions, Operator.StartsWith, "Corp"));
    }

    // ---------- and how the two evaluation paths line up ----------

    /// <summary>
    /// The property the default exists for: the same condition over the same data answers alike in memory and
    /// from a database, because both compare the characters that were stored.
    /// </summary>
    [Fact]
    public void InMemoryAndDatabaseEvaluationAgreeByDefault()
    {
        var stored = $"{SoftHyphen}Acme";
        var condition = new OneValueCondition<string>(Operator.StartsWith, nameof(Minion.Name), "Acme");

        Assert.Equal(0, InMemory(stored, condition, null));
        Assert.Equal(InDatabase(stored, condition), InMemory(stored, condition, null));
    }

    /// <summary>
    /// And what asking for the other rules costs, which is the disagreement the default avoids: a linguistic
    /// comparison treats the soft hyphen as ignorable where SQL compares the characters it stored.
    /// </summary>
    [Fact]
    public void AskingForACultureIsWhatMakesTheTwoPathsDisagree()
    {
        var stored = $"{SoftHyphen}Acme";
        var condition = new OneValueCondition<string>(Operator.StartsWith, nameof(Minion.Name), "Acme");

        Assert.Equal(0, InDatabase(stored, condition));
        Assert.Equal(1, InMemory(stored, condition, Linguistic));
    }

    /// <summary>
    /// Equality routes through the setting like everything else, so under the default it agrees across both paths
    /// </summary>
    [Fact]
    public void EqualityAgreesBetweenInMemoryAndDatabase()
    {
        var stored = $"{SoftHyphen}Acme";
        var condition = new OneValueCondition<string>(Operator.Equals, nameof(Minion.Name), "Acme");

        Assert.Equal(0, InMemory(stored, condition, null));
        Assert.Equal(InDatabase(stored, condition), InMemory(stored, condition, null));

        // and asking for a culture parts them here too
        Assert.Equal(1, InMemory(stored, condition, Linguistic));
    }

    private static int InMemory(string stored, OneValueCondition<string> condition, InquirySettings? settings)
    {
        return new List<Minion> { new() { MinionID = Guid.NewGuid(), Name = stored } }
            .AsQueryable()
            .WithWeequery(settings)
            .BindProperties(Minion.Bindings)
            .ApplyCondition(condition)
            .Build()
            .Count();
    }

    private static int InDatabase(string stored, OneValueCondition<string> condition)
    {
        var context = TestDatabase.Create(TestProvider.Sqlite);
        try
        {
            context.Database.EnsureCreated();
            context.Minions.Add(new Minion { MinionID = Guid.NewGuid(), Name = stored });
            context.SaveChanges();

            return context.Minions
                .WithWeequery()
                .BindProperties(Minion.Bindings)
                .ApplyCondition(condition)
                .Build()
                .Count();
        }
        finally
        {
            TestDatabase.Drop(context);
        }
    }
}
