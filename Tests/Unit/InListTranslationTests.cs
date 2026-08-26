using Microsoft.EntityFrameworkCore;
using Tests.Common;
using Weequery;
using Weequery.Interfaces;

namespace Tests.Unit;

/// <summary>
/// IsIn and IsNotIn are built as 'values.Contains(property)' rather than a chain of ORed equality tests, so that
/// providers can use their native list membership form. These tests pin the resulting SQL, since the point of
/// building it that way is the SQL it produces.
/// </summary>
public class InListTranslationTests
{
    private static string SqlFor(TestProvider provider, ICondition condition)
    {
        using var context = TestDatabase.Create(provider);

        return context.Minions
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition(condition)
            .Build()
            .ToQueryString();
    }

    private static ICondition InList(params decimal[] values)
    {
        return new MultipleValueCondition<decimal>(Operator.IsIn, nameof(Minion.Pay), values.ToList());
    }

    // ---------- native membership rather than a chain of ORs ----------

    [Fact]
    public void SqliteUsesAnInList()
    {
        var sql = TestDatabase.StatementOnly(SqlFor(TestProvider.Sqlite, InList(1m, 2m, 3m)));

        Assert.Contains("IN (", sql);
        Assert.DoesNotContain(" OR ", sql);
    }

    [Fact]
    public void PostgreSqlUsesEqualsAnyWithASingleParameter()
    {
        var queryString = SqlFor(TestProvider.PostgreSql, InList(1m, 2m, 3m));

        Assert.Contains("= ANY (", TestDatabase.StatementOnly(queryString));
        Assert.DoesNotContain(" OR ", TestDatabase.StatementOnly(queryString));

        // The whole list arrives as one parameter, so the statement does not grow with the list
        Assert.Equal(1, TestDatabase.ParameterCount(queryString));
    }

    /// <summary>
    /// The headline win on PostgreSQL: one statement, and so one query plan, no matter how many values the caller
    /// filters on. An OR chain produced a different statement for every length.
    /// </summary>
    [Fact]
    public void PostgreSqlStatementIsIdenticalForEveryListLength()
    {
        var statements = new[] { 1, 2, 5, 50, 500 }
            .Select(count => TestDatabase.StatementOnly(SqlFor(TestProvider.PostgreSql, InList(Enumerable.Range(1, count).Select(value => (decimal)value).ToArray()))))
            .Distinct()
            .ToList();

        Assert.Single(statements);
        Assert.Equal(1, TestDatabase.ParameterCount(SqlFor(TestProvider.PostgreSql, InList(Enumerable.Range(1, 500).Select(value => (decimal)value).ToArray()))));
    }

    [Fact]
    public void SqlServerUsesAnInList()
    {
        var sql = TestDatabase.StatementOnly(SqlFor(TestProvider.SqlServer, InList(1m, 2m, 3m)));

        Assert.Contains("IN (", sql);
        Assert.DoesNotContain(" OR ", sql);
    }

    /// <summary>
    /// SQLite and SQL Server expand the list into individual parameters, but bucket the count, so nearby lengths
    /// still share a statement. Fewer plans than one per length, though not the single plan PostgreSQL manages.
    /// <para>
    /// Note the direction of the rounding: it pads upward, so a list of 9 costs the same parameters as 10. That
    /// means these providers do not escape a server's parameter limit (2100 on SQL Server); PostgreSQL does,
    /// because its whole list is one parameter.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(TestProvider.Sqlite)]
    [InlineData(TestProvider.SqlServer)]
    public void ExpandingProvidersBucketTheParameterCount(TestProvider provider)
    {
        var eight = SqlFor(provider, InList(Enumerable.Range(1, 8).Select(value => (decimal)value).ToArray()));
        var nine = SqlFor(provider, InList(Enumerable.Range(1, 9).Select(value => (decimal)value).ToArray()));

        Assert.Equal(TestDatabase.StatementOnly(eight), TestDatabase.StatementOnly(nine));
        Assert.Equal(TestDatabase.ParameterCount(eight), TestDatabase.ParameterCount(nine));

        // padded upward, so nine values cost ten parameters
        Assert.True(TestDatabase.ParameterCount(nine) > 9, $"expected padding above 9, got {TestDatabase.ParameterCount(nine)}");
    }

    [Fact]
    public void ValuesAreStillParameterizedNotInlined()
    {
        foreach (var provider in new[] { TestProvider.Sqlite, TestProvider.PostgreSql, TestProvider.SqlServer })
        {
            var sql = TestDatabase.StatementOnly(SqlFor(provider, InList(98765m, 43210m)));

            Assert.DoesNotContain("98765", sql);
            Assert.DoesNotContain("43210", sql);
        }
    }

    [Fact]
    public void IsNotInNegatesTheMembershipTest()
    {
        foreach (var provider in new[] { TestProvider.Sqlite, TestProvider.PostgreSql, TestProvider.SqlServer })
        {
            var sql = TestDatabase.StatementOnly(SqlFor(provider, new MultipleValueCondition<decimal>(Operator.IsNotIn, nameof(Minion.Pay), [1m, 2m])));

            Assert.DoesNotContain(" OR ", sql);
        }
    }

    [Fact]
    public void AnEmptyListStillShortCircuitsWithoutAMembershipTest()
    {
        foreach (var provider in new[] { TestProvider.Sqlite, TestProvider.PostgreSql, TestProvider.SqlServer })
        {
            // IN () is not valid SQL, so an empty list must not become a membership test at all
            var sql = TestDatabase.StatementOnly(SqlFor(provider, InList()));

            Assert.DoesNotContain("IN (", sql);
            Assert.DoesNotContain("= ANY", sql);
        }
    }

    [Fact]
    public void ANullablePropertyKeepsItsNullGuard()
    {
        foreach (var provider in new[] { TestProvider.Sqlite, TestProvider.PostgreSql, TestProvider.SqlServer })
        {
            var condition = new MultipleValueCondition<DateTime>(Operator.IsIn, nameof(Minion.FireDate), [new DateTime(2024, 12, 25)]);
            var sql = TestDatabase.StatementOnly(SqlFor(provider, condition));

            Assert.Contains("IS NOT NULL", sql);
        }
    }

    // ---------- and the rows are unchanged ----------

    private static string[] MatchingInMemory(ICondition condition)
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

    [Fact]
    public void MembershipMatchesTheSameRowsInMemory()
    {
        Assert.Equal(["Alice", "David"], MatchingInMemory(InList(12000m, 8000m)));
        Assert.Equal(["Bob", "Charlie"], MatchingInMemory(new MultipleValueCondition<decimal>(Operator.IsNotIn, nameof(Minion.Pay), [12000m, 8000m])));
        Assert.Empty(MatchingInMemory(InList(999m)));
        Assert.Empty(MatchingInMemory(InList()));
        Assert.Equal(["Alice", "Bob", "Charlie", "David"], MatchingInMemory(new MultipleValueCondition<decimal>(Operator.IsNotIn, nameof(Minion.Pay), new List<decimal>())));
    }

    [Fact]
    public void MembershipOnANullablePropertyMatchesTheSameRowsInMemory()
    {
        // Only Bob has a FireDate; the null rows must not match IsIn, and must match IsNotIn
        var fired = new DateTime(2024, 12, 25);

        Assert.Equal(["Bob"], MatchingInMemory(new MultipleValueCondition<DateTime>(Operator.IsIn, nameof(Minion.FireDate), [fired])));
        Assert.Empty(MatchingInMemory(new MultipleValueCondition<DateTime>(Operator.IsNotIn, nameof(Minion.FireDate), [fired]))); // only Bob has a FireDate and he is in the list, the nulls are unknown
    }

    [Fact]
    public void MembershipMatchesTheSameRowsInSqlite()
    {
        var context = TestDatabase.CreateSeeded(TestProvider.Sqlite);
        try
        {
            string[] Names(ICondition condition)
            {
                return context.Minions
                    .WithWeequery()
                    .BindProperties(Minion.Bindings)
                    .ApplyCondition(condition)
                    .Build()
                    .ToList()
                    .Select(minion => minion.Name.Split(' ')[0])
                    .Order()
                    .ToArray();
            }

            Assert.Equal(["Alice", "David"], Names(InList(12000m, 8000m)));
            Assert.Equal(["Bob", "Charlie"], Names(new MultipleValueCondition<decimal>(Operator.IsNotIn, nameof(Minion.Pay), [12000m, 8000m])));
            Assert.Equal(["Bob"], Names(new MultipleValueCondition<DateTime>(Operator.IsIn, nameof(Minion.FireDate), [new DateTime(2024, 12, 25)])));
        }
        finally
        {
            TestDatabase.Drop(context);
        }
    }

    // ---------- the list has a ceiling ----------

    /// <summary>
    /// The list becomes parameters, and a provider will only take so many, so the count is capped where the
    /// condition is built rather than left for the database to refuse. Long past any list a person picks from a
    /// screen: a caller with thousands of values wants a join against a table of them.
    /// </summary>
    private const int MaxValuesInList = 1000;

    private static List<decimal> Values(int count)
    {
        return Enumerable.Range(0, count).Select(i => (decimal)i).ToList();
    }

    /// <summary>
    /// The cap is only worth having where it is: a list right at it has to work, against a real provider, or the
    /// cap is hiding a lower limit somewhere else
    /// </summary>
    [Fact]
    public void AListAtTheCapStillRuns()
    {
        var values = Values(MaxValuesInList);
        values[0] = 12000m; // Alice

        var context = TestDatabase.CreateSeeded(TestProvider.Sqlite);
        try
        {
            var matched = context.Minions
                .WithWeequery()
                .BindProperties(Minion.Bindings)
                .ApplyCondition(new MultipleValueCondition<decimal>(Operator.IsIn, nameof(Minion.Pay), values))
                .Build()
                .ToList();

            Assert.Equal(["Alice Fox"], matched.Select(minion => minion.Name));
        }
        finally
        {
            TestDatabase.Drop(context);
        }
    }

    [Theory]
    [InlineData(Operator.IsIn)]
    [InlineData(Operator.IsNotIn)]
    public void AListPastTheCapIsRefusedWhenTheConditionIsBuilt(Operator op)
    {
        Assert.NotNull(new MultipleValueCondition<decimal>(op, nameof(Minion.Pay), Values(MaxValuesInList)));

        Assert.Throws<WeequeryException>(() => new MultipleValueCondition<decimal>(op, nameof(Minion.Pay), Values(MaxValuesInList + 1)));
    }

    /// <summary>
    /// A query string is where a long list actually arrives from, so the parser has to hold the same line, and say
    /// which field it was
    /// </summary>
    [Fact]
    public void AQueryStringPastTheCapIsRefused()
    {
        string Query(int count) => $"Pay IsIn ({string.Join(", ", Enumerable.Range(0, count))})";

        Assert.NotNull(ConditionFunctions.ParseQuery(Query(MaxValuesInList)));

        Assert.Throws<WeequeryException>(() => ConditionFunctions.ParseQuery(Query(MaxValuesInList + 1)));
    }

    /// <summary>
    /// The other way in off the wire: a packed condition carries its own list, and unpacking it builds a condition
    /// the same way anything else does
    /// </summary>
    [Fact]
    public void APackedConditionPastTheCapIsRefusedWhenUnpacked()
    {
        List<ConditionValue<string>> values = [.. Enumerable.Range(0, MaxValuesInList + 1).Select(i => ConditionValue.Raw(i.ToString()))];

        var packed = new PackedCondition(Operator.IsIn, nameof(Minion.Pay), values, []);

        Assert.Throws<WeequeryException>(() => packed.Unpack());
    }

    [Fact]
    public void TheFluentHelperIsHeldToTheCapToo()
    {
        var conjunction = new ConjunctionCondition(Operator.And, []);

        Assert.Throws<WeequeryException>(() => conjunction.AddIsInTest(nameof(Minion.Pay), Values(MaxValuesInList + 1)));
    }

    /// <summary>
    /// The cap is on the list, not on the other operators: a range still takes its two, and a comparison its one
    /// </summary>
    [Fact]
    public void TheOtherOperatorsKeepTheirOwnCounts()
    {
        Assert.Equal(1, ConditionFunctions.GetNumberOfValuesRequiredForOperation(Operator.Equals).Maximum);
        Assert.Equal(2, ConditionFunctions.GetNumberOfValuesRequiredForOperation(Operator.IsBetween).Maximum);
        Assert.Equal(0, ConditionFunctions.GetNumberOfValuesRequiredForOperation(Operator.IsNull).Maximum);
        Assert.Equal(MaxValuesInList, ConditionFunctions.GetNumberOfValuesRequiredForOperation(Operator.IsIn).Maximum);
    }
}
