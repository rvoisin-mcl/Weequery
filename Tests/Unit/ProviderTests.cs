using Microsoft.EntityFrameworkCore;
using Tests.Common;
using Weequery;
using Weequery.Interfaces;

namespace Tests.Unit;

/// <summary>
/// Weequery builds provider agnostic expression trees, so the same condition should translate on every relational
/// provider. These tests check that against SQLite and PostgreSQL.
/// <para>
/// The SQL generation tests need no server: EF produces SQL from the provider and the model alone. The tests that
/// read rows need a live server, and skip when there is none, see <see cref="TestDatabase"/>.
/// </para>
/// </summary>
public class ProviderTests
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

    private static string SqlFor(TestProvider provider, string query)
    {
        return SqlFor(provider, ConditionFunctions.ParseQuery(query)!);
    }

    // ---------- every operator translates on every provider ----------

    [Theory]
    [MemberData(nameof(TestDatabase.AllProviders), MemberType = typeof(TestDatabase))]
    public void EveryOperatorTranslates(TestProvider provider)
    {
        // Every operator but two. Operator.IsMatch and Operator.DoesNotMatch are deliberately absent: there is no
        // regular expression in standard SQL, so they translate on SQLite and PostgreSQL and not on SQL Server,
        // which is the whole of what makes them different. Where they do and do not work is pinned by IsMatchTests.

        // Producing SQL at all is the assertion: EF throws rather than silently evaluating on the client
        foreach (var query in new[]
        {
            "Pay == 12000",
            "Pay != 12000",
            "Pay > 12000",
            "Pay >= 12000",
            "Pay < 12000",
            "Pay <= 12000",
            "Pay IsBetween (8000, 12000)",
            "Pay IsNotBetween (8000, 12000)",
            "Pay IsIn (8000, 12000)",
            "Pay IsNotIn (8000, 12000)",
            "Name StartsWith 'Al'",
            "Name DoesNotStartWith 'Al'",
            "Name EndsWith 'Fox'",
            "Name DoesNotEndWith 'Fox'",
            "Name Contains 'li'",
            "Name DoesNotContain 'li'",
            "Alias IsNull",
            "Alias IsNotNull",
            "IsActive == true",
            "MinionID == 0f8fad5b-d9cb-469f-a165-70867728950e",
            "HireDate > 2020-01-01",
            "BirthDate IsNull",
            "Classification == Irreplacable",
            "!(Pay > 10000) && (IsActive == true)",
            "((Pay > 15000) || (Pay < 5000)) && (IsActive == true)",
        })
        {
            var sql = SqlFor(provider, query);

            Assert.Contains("SELECT", sql);
        }
    }

    [Theory]
    [MemberData(nameof(TestDatabase.AllProviders), MemberType = typeof(TestDatabase))]
    public void SortingAndPaginationTranslate(TestProvider provider)
    {
        using var context = TestDatabase.Create(provider);

        var sql = context.Minions
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition("IsActive == true")
            .ApplySorts([new Sort(nameof(Minion.IsActive), SortDirection.Ascending), new Sort(nameof(Minion.Pay), SortDirection.Descending)])
            .ApplyPagination(2, 1)
            .Build()
            .ToQueryString();

        var statement = TestDatabase.StatementOnly(sql);

        Assert.Contains("ORDER BY", statement);

        // SQL Server has no LIMIT: it pages with OFFSET/FETCH, which is also why it requires the ORDER BY
        if (provider == TestProvider.SqlServer)
        {
            Assert.Contains("OFFSET", statement);
            Assert.Contains("FETCH NEXT", statement);
        }
        else
        {
            Assert.Contains("LIMIT", statement);
            Assert.Contains("OFFSET", statement);
        }
    }

    [Theory]
    [MemberData(nameof(TestDatabase.AllProviders), MemberType = typeof(TestDatabase))]
    public void SortingOnANullablePropertyTranslates(TestProvider provider)
    {
        using var context = TestDatabase.Create(provider);

        var sql = context.Minions
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplySorts([new Sort(nameof(Minion.FireDate), SortDirection.Ascending)])
            .Build()
            .ToQueryString();

        Assert.Contains("ORDER BY", sql);
    }

    // ---------- values are parameterized on every provider, not just SQLite ----------

    [Theory]
    [MemberData(nameof(TestDatabase.AllProviders), MemberType = typeof(TestDatabase))]
    public void ValuesAreParameterized(TestProvider provider)
    {
        var sql = TestDatabase.StatementOnly(SqlFor(provider, new OneValueCondition<decimal>(Operator.Equals, nameof(Minion.Pay), 98765m)));

        Assert.Contains("@", sql);
        Assert.DoesNotContain("98765", sql);
    }

    [Theory]
    [MemberData(nameof(TestDatabase.AllProviders), MemberType = typeof(TestDatabase))]
    public void TheSameConditionShapeGivesOneStatementWhateverTheValue(TestProvider provider)
    {
        var statements = new[] { 1m, 2m, 999_999m }
            .Select(pay => TestDatabase.StatementOnly(SqlFor(provider, new OneValueCondition<decimal>(Operator.Equals, nameof(Minion.Pay), pay))))
            .Distinct()
            .ToList();

        Assert.Single(statements);
    }

    [Theory]
    [MemberData(nameof(TestDatabase.AllProviders), MemberType = typeof(TestDatabase))]
    public void IsNullNeedsNoParameterOnAnyProvider(TestProvider provider)
    {
        var sql = SqlFor(provider, "Alias IsNull");

        Assert.Contains("IS NULL", TestDatabase.StatementOnly(sql));
    }

    [Theory]
    [MemberData(nameof(TestDatabase.AllProviders), MemberType = typeof(TestDatabase))]
    public void StringValuesAreParameterizedRatherThanInlined(TestProvider provider)
    {
        foreach (var query in new[] { "Name Contains 'Sekrit'", "Name StartsWith 'Sekrit'", "Name EndsWith 'Sekrit'" })
        {
            var sql = TestDatabase.StatementOnly(SqlFor(provider, query));

            Assert.DoesNotContain("Sekrit", sql);
        }
    }

    // ---------- provider specific translations, recorded so a change is visible ----------

    [Fact]
    public void PostgreSqlTranslatesTheStringOperatorsToLike()
    {
        Assert.Contains("LIKE", TestDatabase.StatementOnly(SqlFor(TestProvider.PostgreSql, "Name Contains 'li'")));
        Assert.Contains("LIKE", TestDatabase.StatementOnly(SqlFor(TestProvider.PostgreSql, "Name StartsWith 'Al'")));
        Assert.Contains("LIKE", TestDatabase.StatementOnly(SqlFor(TestProvider.PostgreSql, "Name EndsWith 'Fox'")));
        Assert.Contains("NOT LIKE", TestDatabase.StatementOnly(SqlFor(TestProvider.PostgreSql, "Name DoesNotContain 'li'")));
    }

    [Fact]
    public void SqliteTranslatesContainsToInstr()
    {
        // SQLite has no LIKE based Contains translation, it uses instr
        Assert.Contains("instr", TestDatabase.StatementOnly(SqlFor(TestProvider.Sqlite, "Name Contains 'li'")));
        Assert.Contains("LIKE", TestDatabase.StatementOnly(SqlFor(TestProvider.Sqlite, "Name StartsWith 'Al'")));
    }

    [Fact]
    public void SqlServerTranslatesTheStringOperatorsToLikeWithAnEscapeClause()
    {
        Assert.Contains("LIKE", TestDatabase.StatementOnly(SqlFor(TestProvider.SqlServer, "Name Contains 'li'")));
        Assert.Contains("LIKE", TestDatabase.StatementOnly(SqlFor(TestProvider.SqlServer, "Name StartsWith 'Al'")));
        Assert.Contains("LIKE", TestDatabase.StatementOnly(SqlFor(TestProvider.SqlServer, "Name EndsWith 'Fox'")));

        // The wildcards live in the parameter value, so the statement is the same shape for any search text
        Assert.Contains("ESCAPE", TestDatabase.StatementOnly(SqlFor(TestProvider.SqlServer, "Name Contains 'li'")));
    }

    [Fact]
    public void SqlServerBracketQuotesIdentifiers()
    {
        var statement = TestDatabase.StatementOnly(SqlFor(TestProvider.SqlServer, "Pay > 100"));

        Assert.Contains("[Pay]", statement);
        Assert.DoesNotContain("\"Pay\"", statement);
    }

    /// <summary>
    /// SQLite has no native decimal, so EF compares through its ef_compare helper. PostgreSQL (numeric) and
    /// SQL Server (decimal) compare directly. Worth knowing: the SQLite form cannot use an index on the column.
    /// </summary>
    [Fact]
    public void DecimalComparisonIsNativeExceptOnSqlite()
    {
        Assert.Contains("ef_compare", TestDatabase.StatementOnly(SqlFor(TestProvider.Sqlite, "Pay > 100")));

        var postgres = TestDatabase.StatementOnly(SqlFor(TestProvider.PostgreSql, "Pay > 100"));
        Assert.DoesNotContain("ef_compare", postgres);
        Assert.Contains("\"Pay\" > @", postgres);

        var sqlServer = TestDatabase.StatementOnly(SqlFor(TestProvider.SqlServer, "Pay > 100"));
        Assert.DoesNotContain("ef_compare", sqlServer);
        Assert.Contains("[Pay] > @", sqlServer);
    }

    [Fact]
    public void ProvidersQuoteIdentifiersDifferentlyButFilterTheSameColumns()
    {
        var sqlite = TestDatabase.StatementOnly(SqlFor(TestProvider.Sqlite, "Pay > 100"));
        var postgres = TestDatabase.StatementOnly(SqlFor(TestProvider.PostgreSql, "Pay > 100"));

        Assert.Contains("\"Pay\"", sqlite);
        Assert.Contains("\"Pay\"", postgres);
        Assert.NotEqual(sqlite, postgres);
    }

    // ---------- and the rows come back right, when a server is there ----------

    [Theory]
    [MemberData(nameof(TestDatabase.AllProviders), MemberType = typeof(TestDatabase))]
    public void ConditionsReturnTheCorrectRows(TestProvider provider)
    {
        Assert.SkipUnless(TestDatabase.IsAvailable(provider), TestDatabase.UnavailableReason(provider) ?? string.Empty);

        var context = TestDatabase.CreateSeeded(provider);
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

            Assert.Equal(["Alice", "Charlie"], Names("Pay > 10000"));
            Assert.Equal(["Bob", "David"], Names("!(Pay > 10000) && (IsActive == true)"));
            Assert.Equal(["Bob"], Names("((Pay > 15000) || (Pay < 5000)) && (IsActive == true)"));
            Assert.Equal(["Alice", "David"], Names("Pay IsBetween (8000, 12000)"));
            Assert.Equal(["Bob"], Names("Alias IsNull"));
            Assert.Equal(["Alice", "Charlie", "David"], Names("Alias IsNotNull"));
            Assert.Equal(["Alice", "Bob"], Names("Name IsIn ('Alice Fox', 'Bob Samuelson')"));
            Assert.Equal(["David"], Names("HireDate > 2020-01-01"));
            Assert.Equal(["David"], Names("Classification == Irreplacable"));
            Assert.Equal(["Charlie"], Names("Name StartsWith 'Charlie'"));
        }
        finally
        {
            TestDatabase.Drop(context);
        }
    }

    [Theory]
    [MemberData(nameof(TestDatabase.AllProviders), MemberType = typeof(TestDatabase))]
    public void SortsAndPaginationReturnTheCorrectRows(TestProvider provider)
    {
        Assert.SkipUnless(TestDatabase.IsAvailable(provider), TestDatabase.UnavailableReason(provider) ?? string.Empty);

        var context = TestDatabase.CreateSeeded(provider);
        try
        {
            var byPay = context.Minions
                .WithWeequery()
                .BindProperties(Minion.Bindings)
                .ApplySorts([new Sort(nameof(Minion.Pay), SortDirection.Ascending)])
                .Build()
                .ToList()
                .Select(minion => minion.Name.Split(' ')[0])
                .ToArray();

            Assert.Equal(["Bob", "David", "Alice", "Charlie"], byPay);

            var secondPage = context.Minions
                .WithWeequery()
                .BindProperties(Minion.Bindings)
                .ApplySorts([new Sort(nameof(Minion.Pay), SortDirection.Ascending)])
                .ApplyPagination(2, 1)
                .Build()
                .ToList()
                .Select(minion => minion.Name.Split(' ')[0])
                .ToArray();

            Assert.Equal(["Alice", "Charlie"], secondPage);
        }
        finally
        {
            TestDatabase.Drop(context);
        }
    }

    [Theory]
    [MemberData(nameof(TestDatabase.AllProviders), MemberType = typeof(TestDatabase))]
    public void SubSecondTimestampsRoundTripThroughTheDatabase(TestProvider provider)
    {
        Assert.SkipUnless(TestDatabase.IsAvailable(provider), TestDatabase.UnavailableReason(provider) ?? string.Empty);

        var stamp = new DateTime(2024, 12, 25, 13, 45, 30, 123);

        var context = TestDatabase.Create(provider);
        try
        {
            context.Database.EnsureCreated();
            context.Minions.Add(new Minion { MinionID = Guid.NewGuid(), Name = "Precise Pete", HireDate = stamp });
            context.SaveChanges();

            var packed = (PackedCondition)new OneValueCondition<DateTime>(Operator.Equals, nameof(Minion.HireDate), stamp).Pack();

            var matched = context.Minions
                .WithWeequery()
                .BindProperties(Minion.Bindings)
                .ApplyCondition(packed.Unpack())
                .Build()
                .Count();

            Assert.Equal(1, matched);
        }
        finally
        {
            TestDatabase.Drop(context);
        }
    }

    /// <summary>
    /// LIKE is case sensitive on PostgreSQL but case insensitive for ASCII on SQLite, so the same Weequery
    /// condition can legitimately match different rows. Recorded here so the difference is a known, tested fact
    /// rather than a surprise in production.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestDatabase.AllProviders), MemberType = typeof(TestDatabase))]
    public void CaseSensitivityOfStringMatchingIsProviderDependent(TestProvider provider)
    {
        Assert.SkipUnless(TestDatabase.IsAvailable(provider), TestDatabase.UnavailableReason(provider) ?? string.Empty);

        var context = TestDatabase.CreateSeeded(provider);
        try
        {
            var matched = context.Minions
                .WithWeequery()
                .BindProperties(Minion.Bindings)
                .ApplyCondition("Name StartsWith 'alice'")
                .Build()
                .Count();

            if (provider == TestProvider.PostgreSql)
            {
                Assert.Equal(0, matched);
            }
            else
            {
                Assert.Equal(1, matched);
            }
        }
        finally
        {
            TestDatabase.Drop(context);
        }
    }
}
