using Microsoft.EntityFrameworkCore;
using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// The null rule: a null satisfies nothing except IsNull.
/// <para>
/// Every operator is built as "the property and every nullable link on the way to it have a value" ANDed with the
/// test on the value, and IsNull is the negation of that guard. So a null does not satisfy Equals, and it does not
/// satisfy NotEqual either, because a null is not "not equal to 5", it is unknown. That is what a database answers,
/// and these tests check the two evaluation paths give the same answer rather than hard coding counts, since
/// agreement is the property that matters.
/// </para>
/// </summary>
public class NullSemanticsTests
{
    private static string[] InMemory(string query)
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

    private static string[] InDatabase(string query)
    {
        var context = TestDatabase.CreateSeeded(TestProvider.Sqlite);
        try
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
        finally
        {
            TestDatabase.Drop(context);
        }
    }

    /// <summary>
    /// Queries over columns that hold a null: Alias (string?, null for Bob), FireDate (DateTime?, null for three),
    /// IsVetted (bool?, null for Charlie), ReviewDate (DateOnly?, null for Bob)
    /// </summary>
    public static TheoryData<string> QueriesOverNullableColumns()
    {
        return new TheoryData<string>(
            // equality, both senses
            "Alias == 'Ghost'", "Alias != 'Ghost'",
            "FireDate == 2024-12-25", "FireDate != 2024-12-25",
            "IsVetted == true", "IsVetted != true",
            "ReviewDate == 2024-01-15", "ReviewDate != 2024-01-15",
            // ordering and ranges
            "FireDate > 2020-01-01", "FireDate < 2030-01-01",
            "ReviewDate IsBetween (2023-01-01, 2026-01-01)",
            "ReviewDate IsNotBetween (2023-01-01, 2026-01-01)",
            // membership
            "Alias IsIn ('Ghost', 'Snake')", "Alias IsNotIn ('Ghost', 'Snake')",
            "IsVetted IsIn (true)", "IsVetted IsNotIn (true)",
            // substring, both senses
            "Alias StartsWith 'G'", "Alias DoesNotStartWith 'G'",
            "Alias EndsWith 'e'", "Alias DoesNotEndWith 'e'",
            "Alias Contains 'a'", "Alias DoesNotContain 'a'",
            // the null tests themselves
            "Alias IsNull", "Alias IsNotNull",
            "FireDate IsNull", "FireDate IsNotNull",
            "IsVetted IsNull", "IsVetted IsNotNull");
    }

    /// <summary>
    /// The point of the change: one condition, one answer, wherever it runs
    /// </summary>
    [Theory]
    [MemberData(nameof(QueriesOverNullableColumns))]
    public void InMemoryAgreesWithTheDatabase(string query)
    {
        Assert.Equal(InDatabase(query), InMemory(query));
    }

    /// <summary>
    /// A null belongs to exactly one side of a positive/negative pair, or to neither: never to both. Together with
    /// IsNull accounting for it, the three partition the rows.
    /// </summary>
    [Theory]
    [InlineData("Alias == 'Ghost'", "Alias != 'Ghost'", "Alias IsNull")]
    [InlineData("FireDate == 2024-12-25", "FireDate != 2024-12-25", "FireDate IsNull")]
    [InlineData("IsVetted == true", "IsVetted != true", "IsVetted IsNull")]
    [InlineData("Alias Contains 'a'", "Alias DoesNotContain 'a'", "Alias IsNull")]
    [InlineData("Alias IsIn ('Ghost')", "Alias IsNotIn ('Ghost')", "Alias IsNull")]
    public void APositiveANegativeAndIsNullPartitionTheRows(string positive, string negative, string isNull)
    {
        var matched = InMemory(positive);
        var notMatched = InMemory(negative);
        var nulls = InMemory(isNull);

        // no row is in more than one bucket
        Assert.Empty(matched.Intersect(notMatched));
        Assert.Empty(matched.Intersect(nulls));
        Assert.Empty(notMatched.Intersect(nulls));

        // and between them they account for everything
        Assert.Equal(4, matched.Length + notMatched.Length + nulls.Length);
    }

    [Fact]
    public void ANullIsNotCaughtByAnyNegativeOperator()
    {
        // Bob is the only minion with no Alias, so he must appear in none of these
        foreach (var query in new[]
        {
            "Alias != 'Ghost'",
            "Alias IsNotIn ('Ghost')",
            "Alias DoesNotStartWith 'G'",
            "Alias DoesNotEndWith 'e'",
            "Alias DoesNotContain 'a'",
        })
        {
            Assert.DoesNotContain("Bob", InMemory(query));
        }

        // and in exactly this one
        Assert.Contains("Bob", InMemory("Alias IsNull"));
    }

    /// <summary>
    /// An explicit '!' is not the same question as the negative operator, and the documentation on Operator says
    /// so: it negates the whole test, guard included, so the null rows come back. Both evaluation paths have to
    /// agree on that too.
    /// </summary>
    [Theory]
    [InlineData("Alias != 'Ghost'", "!(Alias == 'Ghost')")]
    [InlineData("Alias DoesNotContain 'a'", "!(Alias Contains 'a')")]
    [InlineData("Alias IsNotIn ('Ghost')", "!(Alias IsIn ('Ghost'))")]
    public void NegatingAConditionIsNotTheSameAsTheNegativeOperator(string negativeOperator, string negatedCondition)
    {
        // Bob has no Alias, so the operator leaves him out and the negation takes him back
        Assert.DoesNotContain("Bob", InMemory(negativeOperator));
        Assert.Contains("Bob", InMemory(negatedCondition));

        Assert.Equal(InDatabase(negativeOperator), InMemory(negativeOperator));
        Assert.Equal(InDatabase(negatedCondition), InMemory(negatedCondition));
    }

    /// <summary>
    /// With nothing to be excluded by, every row that has a value qualifies. The rows that do not are still out,
    /// which is the part the summary on IsNotIn used to have backwards.
    /// </summary>
    [Fact]
    public void AnEmptyIsNotInTakesEveryRowThatHasAValue()
    {
        Assert.Equal(InMemory("Alias IsNotNull"), InMemory("Alias IsNotIn ()"));

        Assert.Equal(InDatabase("Alias IsNotIn ()"), InMemory("Alias IsNotIn ()"));
    }

    /// <summary>
    /// A column that cannot be null is unaffected: no guard is emitted and the negative operators still take
    /// everything the positive one leaves
    /// </summary>
    [Fact]
    public void ANonNullableColumnIsUnaffected()
    {
        Assert.Equal(4, InMemory("Name == 'Alice Fox'").Length + InMemory("Name != 'Alice Fox'").Length);
        Assert.Equal(4, InMemory("IsActive == true").Length + InMemory("IsActive != true").Length);
        Assert.Equal(4, InMemory("Pay > 10000").Length + InMemory("Pay <= 10000").Length);
    }

    [Fact]
    public void NoGuardIsEmittedForANonNullableColumn()
    {
        using var context = TestDatabase.Create(TestProvider.Sqlite);

        var statement = TestDatabase.StatementOnly(context.Minions
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition("Name == 'Alice Fox'")
            .Build()
            .ToQueryString());

        Assert.DoesNotContain("IS NOT NULL", statement);
    }

    [Fact]
    public void AGuardIsEmittedForANullableColumn()
    {
        using var context = TestDatabase.Create(TestProvider.Sqlite);

        var statement = TestDatabase.StatementOnly(context.Minions
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition("Alias != 'Ghost'")
            .Build()
            .ToQueryString());

        Assert.Contains("IS NOT NULL", statement);
    }

    // ---------- and for a member reached through a nullable ----------

    private static readonly BindingRequest[] DerivedBindings =
    [
        new(nameof(Minion.Name), null),
        new(nameof(Minion.FireDate), null),
        new("FireDate.Year", "FireYear"),
    ];

    private static string[] DerivedInMemory(string query)
    {
        return MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(DerivedBindings)
            .ApplyCondition(query)
            .Build()
            .ToList()
            .Select(minion => minion.Name.Split(' ')[0])
            .Order()
            .ToArray();
    }

    private static string[] DerivedInDatabase(string query)
    {
        var context = TestDatabase.CreateSeeded(TestProvider.Sqlite);
        try
        {
            return context.Minions
                .WithWeequery()
                .BindProperties(DerivedBindings)
                .ApplyCondition(query)
                .Build()
                .ToList()
                .Select(minion => minion.Name.Split(' ')[0])
                .Order()
                .ToArray();
        }
        finally
        {
            TestDatabase.Drop(context);
        }
    }

    [Theory]
    [InlineData("FireYear == 2024")]
    [InlineData("FireYear != 2024")]
    [InlineData("FireYear > 2000")]
    [InlineData("FireYear IsNull")]
    [InlineData("FireYear IsNotNull")]
    [InlineData("FireYear IsIn (2024)")]
    [InlineData("FireYear IsNotIn (2024)")]
    public void ADerivedMemberAgreesBetweenMemoryAndTheDatabase(string query)
    {
        Assert.Equal(DerivedInDatabase(query), DerivedInMemory(query));
    }

    [Fact]
    public void ADerivedMembersNullTestAnswersForItsParent()
    {
        Assert.Equal(DerivedInMemory("FireDate IsNull"), DerivedInMemory("FireYear IsNull"));
        Assert.Equal(DerivedInMemory("FireDate IsNotNull"), DerivedInMemory("FireYear IsNotNull"));
    }
}
