using Microsoft.EntityFrameworkCore;
using Tests.Common;
using Weequery;
using Weequery.Interfaces;

namespace Tests.Unit;

/// <summary>
/// bool and bool? bindings.
/// <para>
/// Minion.IsActive is a plain bool and Minion.IsVetted is a bool?, with the shared test set carrying true, false
/// and null for the latter, so both the plain and the Nullable&lt;&gt; branch of every supported operator has a row
/// that exercises it.
/// </para>
/// <para>
/// Only the operators that mean something for a truth value are supported. Ordering one truth value against
/// another is refused, since it cannot express anything a caller meant.
/// </para>
/// </summary>
public class NullableBoolTests
{
    private static string[] Matching(string query)
    {
        return MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition(query)
            .Build()
            .ToList()
            .Select(minion => minion.Name.Split(' ')[0])
            .Order()
            .ToArray();
    }

    private static string[] Matching(ICondition condition)
    {
        return MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition(condition)
            .Build()
            .ToList()
            .Select(minion => minion.Name.Split(' ')[0])
            .Order()
            .ToArray();
    }

    // ---------- the nullable branch of every supported operator ----------
    // IsVetted: Alice true, Bob false, Charlie null, David true

    [Fact]
    public void EqualsMatchesOnlyRowsThatHaveTheValue()
    {
        Assert.Equal(["Alice", "David"], Matching("IsVetted == true"));
        Assert.Equal(["Bob"], Matching("IsVetted == false"));
    }

    /// <summary>
    /// The null row must not match either sense of equality, which is the HasValue guard doing its job
    /// </summary>
    [Fact]
    public void ANullNeverEqualsATruthValue()
    {
        Assert.DoesNotContain("Charlie", Matching("IsVetted == true"));
        Assert.DoesNotContain("Charlie", Matching("IsVetted == false"));
    }

    /// <summary>
    /// NotEqual excludes the null row, because a null is not "not equal to true", it is unknown. That is what a
    /// database answers for &lt;&gt; against a null, and the library follows it.
    /// </summary>
    [Fact]
    public void NotEqualExcludesTheNullRow()
    {
        Assert.Equal(["Bob"], Matching("IsVetted != true"));
        Assert.Equal(["Alice", "David"], Matching("IsVetted != false"));

        // only IsNull finds it
        Assert.Equal(["Charlie"], Matching("IsVetted IsNull"));
    }

    [Fact]
    public void NullTestsFindTheNullRow()
    {
        Assert.Equal(["Charlie"], Matching("IsVetted IsNull"));
        Assert.Equal(["Alice", "Bob", "David"], Matching("IsVetted IsNotNull"));
    }

    [Fact]
    public void TheEqualsNullShorthandAgreesWithIsNull()
    {
        Assert.Equal(Matching("IsVetted IsNull"), Matching("IsVetted == null"));
        Assert.Equal(Matching("IsVetted IsNotNull"), Matching("IsVetted != null"));
    }

    /// <summary>
    /// IsIn used to throw for a bool binding, because the bool builder never reached the shared operator logic.
    /// A single-element IsIn is what a generic multi-select filter produces for a boolean column.
    /// </summary>
    [Fact]
    public void IsInMatchesAnyOfTheListedValues()
    {
        Assert.Equal(["Alice", "David"], Matching("IsVetted IsIn (true)"));
        Assert.Equal(["Bob"], Matching("IsVetted IsIn (false)"));
        Assert.Equal(["Alice", "Bob", "David"], Matching("IsVetted IsIn (true, false)"));
    }

    [Fact]
    public void IsNotInExcludesTheListedValuesAndTheNullRow()
    {
        // Charlie's null is unknown rather than "not in the list", so it does not come back, matching NOT IN
        Assert.Equal(["Bob"], Matching("IsVetted IsNotIn (true)"));
        Assert.Empty(Matching("IsVetted IsNotIn (true, false)"));
    }

    [Fact]
    public void AnEmptyInListStillShortCircuits()
    {
        Assert.Empty(Matching("IsVetted IsIn ()"));

        // Nothing to be excluded by, so every row that has a value qualifies. The null still does not.
        Assert.Equal(["Alice", "Bob", "David"], Matching("IsVetted IsNotIn ()"));
    }

    // ---------- the plain bool branch, for contrast ----------

    [Fact]
    public void APlainBoolSupportsTheSameOperators()
    {
        Assert.Equal(["Alice", "Bob", "David"], Matching("IsActive == true"));
        Assert.Equal(["Charlie"], Matching("IsActive == false"));
        Assert.Equal(["Charlie"], Matching("IsActive != true"));
        Assert.Equal(["Alice", "Bob", "David"], Matching("IsActive IsIn (true)"));
        Assert.Equal(["Charlie"], Matching("IsActive IsNotIn (true)"));
    }

    [Fact]
    public void APlainBoolRejectsTheNullTests()
    {
        // A bool column cannot be null, so asking is a mistake worth reporting
        foreach (var query in new[] { "IsActive IsNull", "IsActive IsNotNull" })
        {
            Assert.Throws<WeequeryException>(() => Matching(query));
        }
    }

    // ---------- ordering a truth value is refused ----------

    [Theory]
    [InlineData("IsVetted > true")]
    [InlineData("IsVetted >= true")]
    [InlineData("IsVetted < true")]
    [InlineData("IsVetted <= true")]
    [InlineData("IsVetted IsBetween (false, true)")]
    [InlineData("IsVetted IsNotBetween (false, true)")]
    [InlineData("IsActive > false")]
    public void OrderingOperatorsAreRefusedWithAClearMessage(string query)
    {
        Assert.Throws<WeequeryException>(() => Matching(query));
    }

    // ---------- typed conditions, and the value text ----------

    [Fact]
    public void ATypedNullableBoolConditionBehavesTheSame()
    {
        Assert.Equal(["Alice", "David"], Matching(new OneValueCondition<bool>(Operator.Equals, nameof(Minion.IsVetted), true)));
        Assert.Equal(["Charlie"], Matching(new NoValueCondition(Operator.IsNull, nameof(Minion.IsVetted))));
        Assert.Equal(["Alice", "Bob", "David"], Matching(new MultipleValueCondition<bool>(Operator.IsIn, nameof(Minion.IsVetted), [true, false])));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("TRUE")]
    public void TheValueTextIsCaseInsensitive(string text)
    {
        Assert.Equal(["Alice", "David"], Matching($"IsVetted == {text}"));
    }

    [Fact]
    public void ANonBooleanValueIsReported()
    {
        Assert.Throws<WeequeryException>(() => Matching("IsVetted == maybe"));
    }

    [Fact]
    public void ANullableBoolConditionRoundTripsThroughTheQueryLanguage()
    {
        foreach (var query in new[] { "IsVetted == true", "IsVetted IsNull", "IsVetted IsIn (true, false)", "IsVetted IsNotIn (false)" })
        {
            var condition = ConditionFunctions.ParseQuery(query)!;

            Assert.Equal(Matching(condition), Matching(ConditionFunctions.ParseQuery(condition.ToQuery())!));
        }
    }

    // ---------- and it translates on a real provider ----------

    [Theory]
    [MemberData(nameof(TestDatabase.AllProviders), MemberType = typeof(TestDatabase))]
    public void EverySupportedOperatorTranslates(TestProvider provider)
    {
        using var context = TestDatabase.Create(provider);

        foreach (var query in new[]
        {
            "IsVetted == true",
            "IsVetted != true",
            "IsVetted IsNull",
            "IsVetted IsNotNull",
            "IsVetted IsIn (true, false)",
            "IsVetted IsNotIn (true)",
        })
        {
            var sql = context.Minions
                .WithWeequery()
                .BindProperties(Minion.Bindings)
                .ApplyCondition(query)
                .Build()
                .ToQueryString();

            Assert.Contains("SELECT", sql);
        }
    }

    [Fact]
    public void TheNullableBranchKeepsItsNullGuardInSql()
    {
        using var context = TestDatabase.Create(TestProvider.Sqlite);

        var statement = TestDatabase.StatementOnly(context.Minions
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition("IsVetted == true")
            .Build()
            .ToQueryString());

        Assert.Contains("IS NOT NULL", statement);
    }

    [Fact]
    public void RowsComeBackCorrectlyFromARealDatabase()
    {
        var context = TestDatabase.CreateSeeded(TestProvider.Sqlite);
        try
        {
            string[] Names(string query)
            {
                return context.Minions
                    .WithWeequery()
                    .BindProperties(Minion.Bindings)
                    .ApplyCondition(query)
                    .Build()
                    .ToList()
                    .Select(minion => minion.Name.Split(' ')[0])
                    .Order()
                    .ToArray();
            }

            Assert.Equal(["Alice", "David"], Names("IsVetted == true"));
            Assert.Equal(["Bob"], Names("IsVetted == false"));
            Assert.Equal(["Charlie"], Names("IsVetted IsNull"));
            Assert.Equal(["Alice", "Bob", "David"], Names("IsVetted IsNotNull"));
            Assert.Equal(["Alice", "Bob", "David"], Names("IsVetted IsIn (true, false)"));
            Assert.Equal(["Bob"], Names("IsVetted IsNotIn (true)"));
        }
        finally
        {
            TestDatabase.Drop(context);
        }
    }
}
