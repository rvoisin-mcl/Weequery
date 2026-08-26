using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// Characterization tests for the substring operators. These record what Weequery does today, they are not a
/// statement that the behaviour is desirable.
/// <para>
/// The substring operators are built from the framework's own string methods, so the comparison rules come from
/// wherever the query is evaluated. In memory, StartsWith and EndsWith are culture sensitive while Contains is
/// ordinal; against a database the column's collation decides. The behaviour is documented for callers on
/// <see cref="Operator"/> and the reasoning is on StringExpressionBuilder.
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

    private static IQueryable<Minion> Named(string name)
    {
        return new List<Minion> { new() { MinionID = Guid.NewGuid(), Name = name } }.AsQueryable();
    }

    private static int MatchesInMemory(IQueryable<Minion> minions, Operator op, string value)
    {
        return minions
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition(new OneValueCondition<string>(op, nameof(Minion.Name), value))
            .Build()
            .Count();
    }

    // ---------- in memory: StartsWith and EndsWith are culture sensitive, Contains is ordinal ----------

    [Fact]
    public void StartsWithIgnoresAnIgnorableCharacterInMemory()
    {
        // "Acme" starts with "Ac<SHY>me" under linguistic comparison, because the soft hyphen is ignorable
        Assert.Equal(1, MatchesInMemory(Named("Acme"), Operator.StartsWith, $"Ac{SoftHyphen}me"));
    }

    [Fact]
    public void EndsWithIgnoresAnIgnorableCharacterInMemory()
    {
        Assert.Equal(1, MatchesInMemory(Named("Acme"), Operator.EndsWith, $"Ac{SoftHyphen}me"));
    }

    [Fact]
    public void ContainsDoesNotIgnoreAnIgnorableCharacterInMemory()
    {
        // Ordinal, so the soft hyphen has to be there literally
        Assert.Equal(0, MatchesInMemory(Named("Acme"), Operator.Contains, $"Ac{SoftHyphen}me"));
    }

    /// <summary>
    /// The inconsistency stated outright: for one value and one filter, StartsWith matches but Contains does not,
    /// which cannot both be right. Anything that starts with a string necessarily contains it.
    /// </summary>
    [Fact]
    public void StartsWithAndContainsDisagreeOnTheSameValueAndFilter()
    {
        var minions = Named("Acme");
        var filter = $"Ac{SoftHyphen}me";

        Assert.Equal(1, MatchesInMemory(minions, Operator.StartsWith, filter));
        Assert.Equal(0, MatchesInMemory(minions, Operator.Contains, filter));
    }

    [Fact]
    public void NegatedFormsFollowTheirPositiveCounterparts()
    {
        var minions = Named("Acme");
        var filter = $"Ac{SoftHyphen}me";

        Assert.Equal(0, MatchesInMemory(minions, Operator.DoesNotStartWith, filter));
        Assert.Equal(0, MatchesInMemory(minions, Operator.DoesNotEndWith, filter));
        Assert.Equal(1, MatchesInMemory(minions, Operator.DoesNotContain, filter));
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

    // ---------- and the two evaluation paths can disagree with each other ----------

    /// <summary>
    /// The consequence that matters: the same condition over the same data returns a different answer in memory
    /// than it does from a database, because SQL compares the stored characters rather than comparing
    /// linguistically.
    /// </summary>
    [Fact]
    public void InMemoryAndDatabaseEvaluationCanDisagree()
    {
        var stored = $"{SoftHyphen}Acme";
        var condition = new OneValueCondition<string>(Operator.StartsWith, nameof(Minion.Name), "Acme");

        var inMemory = new List<Minion> { new() { MinionID = Guid.NewGuid(), Name = stored } }
            .AsQueryable()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition(condition)
            .Build()
            .Count();

        var context = TestDatabase.Create(TestProvider.Sqlite);
        try
        {
            context.Database.EnsureCreated();
            context.Minions.Add(new Minion { MinionID = Guid.NewGuid(), Name = stored });
            context.SaveChanges();

            var inDatabase = context.Minions
                .WithWeequery()
                .BindProperties(Minion.Bindings)
                .ApplyCondition(condition)
                .Build()
                .Count();

            Assert.Equal(1, inMemory);
            Assert.Equal(0, inDatabase);
        }
        finally
        {
            TestDatabase.Drop(context);
        }
    }

    /// <summary>
    /// Equality is not affected by the linguistic/ordinal split, so it agrees across both paths
    /// </summary>
    [Fact]
    public void EqualityAgreesBetweenInMemoryAndDatabase()
    {
        var stored = $"{SoftHyphen}Acme";
        var condition = new OneValueCondition<string>(Operator.Equals, nameof(Minion.Name), "Acme");

        var inMemory = new List<Minion> { new() { MinionID = Guid.NewGuid(), Name = stored } }
            .AsQueryable()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition(condition)
            .Build()
            .Count();

        var context = TestDatabase.Create(TestProvider.Sqlite);
        try
        {
            context.Database.EnsureCreated();
            context.Minions.Add(new Minion { MinionID = Guid.NewGuid(), Name = stored });
            context.SaveChanges();

            var inDatabase = context.Minions
                .WithWeequery()
                .BindProperties(Minion.Bindings)
                .ApplyCondition(condition)
                .Build()
                .Count();

            Assert.Equal(0, inMemory);
            Assert.Equal(inMemory, inDatabase);
        }
        finally
        {
            TestDatabase.Drop(context);
        }
    }
}
