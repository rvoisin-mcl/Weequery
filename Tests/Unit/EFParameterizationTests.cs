using Microsoft.EntityFrameworkCore;
using Tests.Common;
using Weequery;
using Weequery.Interfaces;

namespace Tests.Unit;

/// <summary>
/// Condition values must reach the database as parameters, not as literals baked into the SQL. Literals give a
/// distinct SQL string, and so a distinct query plan, for every distinct filter value.
/// <para>
/// These tests read the SQL that EF Core generates. Producing it at all also proves the expression is translatable,
/// since EF throws rather than falling back to client evaluation.
/// </para>
/// </summary>
public class EFParameterizationTests
{
    private static DBContext Context()
    {
        // No connection is opened: ToQueryString only compiles the query
        return new DBContext(new());
    }

    private static string QueryStringFor(ICondition condition)
    {
        using var context = Context();

        return context.Minions
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition(condition)
            .Build()
            .ToQueryString();
    }

    /// <summary>
    /// Strip the ".param set @X value" preamble, leaving only the SQL. Two queries with the same SQL share a plan
    /// however much their parameter values differ.
    /// </summary>
    private static string SqlOnly(string queryString)
    {
        return string.Join(
            "\n",
            queryString
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => (line.Length > 0) && (!line.StartsWith(".param"))));
    }

    private static string WhereClauseFor(ICondition condition)
    {
        return SqlOnly(QueryStringFor(condition));
    }

    // ---------- the point of the exercise: one plan for every value ----------

    /// <summary>
    /// The regression this guards: with values passed as constants the generated SQL was
    /// 'WHERE "m"."Pay" = 12000.0', a different string for every value the caller filtered on.
    /// </summary>
    [Fact]
    public void TheSameConditionShapeProducesIdenticalSqlWhateverTheValue()
    {
        var sql = new[] { 1m, 2m, 999_999m }
            .Select(pay => WhereClauseFor(new OneValueCondition<decimal>(Operator.Equals, nameof(Minion.Pay), pay)))
            .Distinct()
            .ToList();

        Assert.Single(sql);
    }

    [Fact]
    public void TheSameConditionShapeProducesIdenticalSqlWhateverTheStringValue()
    {
        var sql = new[] { "a", "something else", "" }
            .Select(name => WhereClauseFor(new OneValueCondition<string>(Operator.Contains, nameof(Minion.Name), name)))
            .Distinct()
            .ToList();

        Assert.Single(sql);
    }

    [Fact]
    public void TheSameConditionShapeProducesIdenticalSqlWhateverTheDate()
    {
        var sql = new[] { new DateTime(2001, 1, 1), new DateTime(2024, 6, 30) }
            .Select(date => WhereClauseFor(new OneValueCondition<DateTime>(Operator.GreaterThan, nameof(Minion.HireDate), date)))
            .Distinct()
            .ToList();

        Assert.Single(sql);
    }

    // ---------- values appear as parameters, never inline ----------

    [Fact]
    public void NumericValueIsParameterized()
    {
        var sql = WhereClauseFor(new OneValueCondition<decimal>(Operator.Equals, nameof(Minion.Pay), 12345m));

        Assert.Contains("@", sql);
        Assert.DoesNotContain("12345", sql);
    }

    [Fact]
    public void StringValueIsParameterized()
    {
        var sql = WhereClauseFor(new OneValueCondition<string>(Operator.Equals, nameof(Minion.Name), "Sekrit Squirrel"));

        Assert.Contains("@", sql);
        Assert.DoesNotContain("Sekrit Squirrel", sql);
    }

    [Fact]
    public void DateValueIsParameterized()
    {
        var sql = WhereClauseFor(new OneValueCondition<DateTime>(Operator.GreaterThan, nameof(Minion.HireDate), new DateTime(2019, 3, 17)));

        Assert.Contains("@", sql);
        Assert.DoesNotContain("2019", sql);
    }

    [Fact]
    public void EnumValueIsParameterized()
    {
        var sql = WhereClauseFor(new OneValueCondition<Classification>(Operator.Equals, nameof(Minion.Classification), Classification.Irreplacable));

        Assert.Contains("@", sql);
    }

    [Theory]
    [InlineData(Operator.StartsWith)]
    [InlineData(Operator.DoesNotStartWith)]
    [InlineData(Operator.EndsWith)]
    [InlineData(Operator.DoesNotEndWith)]
    [InlineData(Operator.Contains)]
    [InlineData(Operator.DoesNotContain)]
    public void StringOperatorsParameterizeTheirValue(Operator op)
    {
        var sql = WhereClauseFor(new OneValueCondition<string>(op, nameof(Minion.Name), "Zaphod"));

        Assert.Contains("@", sql);
        Assert.DoesNotContain("Zaphod", sql);
    }

    [Theory]
    [InlineData(Operator.LessThan)]
    [InlineData(Operator.LessThanOrEqual)]
    [InlineData(Operator.GreaterThan)]
    [InlineData(Operator.GreaterThanOrEqual)]
    [InlineData(Operator.NotEqual)]
    public void ComparisonOperatorsParameterizeTheirValue(Operator op)
    {
        var sql = WhereClauseFor(new OneValueCondition<decimal>(op, nameof(Minion.Pay), 54321m));

        Assert.Contains("@", sql);
        Assert.DoesNotContain("54321", sql);
    }

    [Fact]
    public void BothEndsOfARangeAreParameterized()
    {
        var sql = QueryStringFor(new TwoValueCondition<decimal>(Operator.IsBetween, nameof(Minion.Pay), 111m, 222m));

        Assert.Equal(2, sql.Split(".param set").Length - 1);
        Assert.DoesNotContain("111", SqlOnly(sql));
        Assert.DoesNotContain("222", SqlOnly(sql));
    }

    [Fact]
    public void EveryValueInAnInListIsParameterized()
    {
        var sql = QueryStringFor(new MultipleValueCondition<decimal>(Operator.IsIn, nameof(Minion.Pay), [11m, 22m, 33m]));

        Assert.Equal(3, sql.Split(".param set").Length - 1);
        Assert.DoesNotContain("11", SqlOnly(sql));
    }

    [Fact]
    public void ValuesInsideConjunctionsAndNegationsAreParameterized()
    {
        var condition = new ConjunctionCondition(Operator.And,
        [
            new OneValueCondition<decimal>(Operator.GreaterThan, nameof(Minion.Pay), 7777m),
            new NotCondition(Operator.Not, new OneValueCondition<string>(Operator.Contains, nameof(Minion.Name), "Nemesis")),
        ]);

        var sql = QueryStringFor(condition);

        Assert.DoesNotContain("7777", SqlOnly(sql));
        Assert.DoesNotContain("Nemesis", SqlOnly(sql));
    }

    // ---------- structural constants must stay constants ----------

    [Fact]
    public void IsNullStaysABareIsNullWithNoParameter()
    {
        // Parameterizing this would give '= @p', which never matches a NULL
        var sql = QueryStringFor(new NoValueCondition(Operator.IsNull, nameof(Minion.Alias)));

        Assert.Contains("IS NULL", SqlOnly(sql));
        Assert.DoesNotContain(".param set", sql);
    }

    [Fact]
    public void IsNotNullStaysABareIsNotNullWithNoParameter()
    {
        var sql = QueryStringFor(new NoValueCondition(Operator.IsNotNull, nameof(Minion.Alias)));

        Assert.Contains("IS NOT NULL", SqlOnly(sql));
        Assert.DoesNotContain(".param set", sql);
    }

    [Fact]
    public void NullableValueComparisonKeepsItsNullGuardAndParameterizesTheValue()
    {
        var sql = QueryStringFor(new OneValueCondition<DateTime>(Operator.Equals, nameof(Minion.FireDate), new DateTime(2024, 12, 25)));

        Assert.Contains("IS NOT NULL", SqlOnly(sql));
        Assert.Contains("@", SqlOnly(sql));
        Assert.DoesNotContain("2024", SqlOnly(sql));
    }

    [Fact]
    public void AnEmptyInListNeedsNoParameter()
    {
        var sql = QueryStringFor(new MultipleValueCondition<decimal>(Operator.IsIn, nameof(Minion.Pay), new List<decimal>()));

        Assert.DoesNotContain(".param set", sql);
    }

    // ---------- and it still returns the right rows ----------

    [Fact]
    public void ParameterizedQueriesReturnTheCorrectRowsAgainstARealDatabase()
    {
        var context = Context();
        try
        {
            context.Database.EnsureCreated();
            foreach (var minion in MinionTestData.Minions()) { context.Minions.Add(minion); }
            context.SaveChanges();

            var names = context.Minions
                .WithWeequery()
                .BindProperties(Minion.Bindings)
                .ApplyCondition("!(Pay > 10000) && (IsActive == true)")
                .ApplySorts([new Sort(nameof(Minion.Pay), SortDirection.Ascending)])
                .Build()
                .Select(minion => minion.Name)
                .ToList();

            Assert.Equal(["Bob Samuelson", "David Edgars"], names);
        }
        finally
        {
            context.Database.EnsureDeleted();
            context.Dispose();
        }
    }

    [Fact]
    public void ParameterizedStringAndRangeQueriesReturnTheCorrectRowsAgainstARealDatabase()
    {
        var context = Context();
        try
        {
            context.Database.EnsureCreated();
            foreach (var minion in MinionTestData.Minions()) { context.Minions.Add(minion); }
            context.SaveChanges();

            IQueryable<Minion> Query(string query)
            {
                return context.Minions.WithWeequery().BindProperties(Minion.Bindings).ApplyCondition(query).Build();
            }

            Assert.Equal(2, Query("Name Contains 'li'").Count());
            Assert.Equal(2, Query("Pay IsBetween (8000, 12000)").Count());
            Assert.Equal(1, Query("Alias IsNull").Count());
            Assert.Equal(2, Query("Name IsIn ('Alice Fox', 'Bob Samuelson')").Count());
            Assert.Equal(1, Query("HireDate > 2020-01-01").Count());
        }
        finally
        {
            context.Database.EnsureDeleted();
            context.Dispose();
        }
    }
}
