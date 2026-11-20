using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Tests.Common;

public enum TestProvider
{
    /// <summary>File backed SQLite, always available</summary>
    Sqlite,

    /// <summary>PostgreSQL, needs a reachable server</summary>
    PostgreSql,

    /// <summary>SQL Server, needs a reachable server (LocalDB counts)</summary>
    SqlServer,
}

/// <summary>
/// Creates databases for the provider backed tests.
/// <para>
/// SQLite needs nothing, so it always runs. PostgreSQL and SQL Server need a server, so they are opt in: set the
/// matching environment variable to a connection string and those tests start running. With the variable unset
/// they report as skipped rather than failing, so the suite stays green on a machine with neither installed.
/// </para>
/// <list type="table">
/// <item><term>WEEQUERY_TEST_POSTGRES</term><description>eg. Host=localhost;Username=postgres;Password=secret</description></item>
/// <item><term>WEEQUERY_TEST_SQLSERVER</term><description>eg. Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True</description></item>
/// </list>
/// <para>
/// The account needs permission to create and drop databases, since each run uses a throwaway one.
/// </para>
/// <para>
/// Note that SQL generation can be checked for any provider with no server at all, since EF only needs the
/// provider and the model to produce SQL. Only tests that actually read rows need a live server.
/// </para>
/// </summary>
public static class TestDatabase
{
    /// <summary>
    /// Every provider, for tests that only inspect generated SQL
    /// </summary>
    public static TheoryData<TestProvider> AllProviders()
    {
        return new TheoryData<TestProvider>(TestProvider.Sqlite, TestProvider.PostgreSql, TestProvider.SqlServer);
    }

    /// <summary>
    /// The providers that talk to a real server, so are opt in
    /// </summary>
    public static TheoryData<TestProvider> ServerProviders()
    {
        return new TheoryData<TestProvider>(TestProvider.PostgreSql, TestProvider.SqlServer);
    }

    public static string ConnectionStringVariableFor(TestProvider provider)
    {
        return provider switch
        {
            TestProvider.PostgreSql => "WEEQUERY_TEST_POSTGRES",
            TestProvider.SqlServer => "WEEQUERY_TEST_SQLSERVER",
            _ => string.Empty,
        };
    }

    public static string? ConfiguredConnectionString(TestProvider provider)
    {
        var variable = ConnectionStringVariableFor(provider);
        if (variable.Length == 0) { return null; }

        var value = Environment.GetEnvironmentVariable(variable);

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Probed once per provider per test run. A wrong or dead connection string should cost one timeout, not one
    /// per test.
    /// </summary>
    private static readonly Dictionary<TestProvider, Lazy<string?>> ServerChecks = new()
    {
        { TestProvider.PostgreSql, new(() => ProbeServer(TestProvider.PostgreSql)) },
        { TestProvider.SqlServer, new(() => ProbeServer(TestProvider.SqlServer)) },
    };

    public static string DisplayName(TestProvider provider)
    {
        return provider switch
        {
            TestProvider.PostgreSql => "PostgreSQL",
            TestProvider.SqlServer => "SQL Server",
            _ => provider.ToString(),
        };
    }

    private static string? ProbeServer(TestProvider provider)
    {
        var variable = ConnectionStringVariableFor(provider);

        var connectionString = ConfiguredConnectionString(provider);
        if (connectionString is null)
        {
            return $"{variable} is not set, so there is no {DisplayName(provider)} to test against";
        }

        try
        {
            // Short timeout: this is a reachability check, not a real workload
            using var connection = OpenProbeConnection(provider, connectionString);

            return null;
        }
        catch (Exception ex)
        {
            return $"{DisplayName(provider)} at {variable} could not be reached: {ex.Message}";
        }
    }

    private static IDisposable OpenProbeConnection(TestProvider provider, string connectionString)
    {
        switch (provider)
        {
            case TestProvider.PostgreSql:
                {
                    var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Timeout = 5 }.ConnectionString);
                    connection.Open();
                    return connection;
                }

            case TestProvider.SqlServer:
                {
                    var connection = new SqlConnection(new SqlConnectionStringBuilder(connectionString) { ConnectTimeout = 5 }.ConnectionString);
                    connection.Open();
                    return connection;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(provider));
        }
    }

    /// <summary>
    /// Why the provider cannot be used, or null when it can
    /// </summary>
    public static string? UnavailableReason(TestProvider provider)
    {
        return ServerChecks.TryGetValue(provider, out var check) ? check.Value : null;
    }

    public static bool IsAvailable(TestProvider provider)
    {
        return UnavailableReason(provider) is null;
    }

    /// <summary>
    /// Only ever used to let EF build a model and generate SQL when no server is configured, never connected to
    /// </summary>
    private static string SqlGenerationOnlyConnectionString(TestProvider provider)
    {
        return provider switch
        {
            TestProvider.PostgreSql => "Host=localhost;Database=weequery_sql_generation_only",
            TestProvider.SqlServer => "Server=localhost;Database=weequery_sql_generation_only;Trusted_Connection=True;TrustServerCertificate=True",
            _ => string.Empty,
        };
    }

    /// <summary>
    /// Build a context for the provider. No connection is opened, so this is safe to call for a server backed
    /// provider with nothing running: enough to let EF produce SQL.
    /// </summary>
    /// <param name="provider"></param>
    /// <returns></returns>
    public static DBContext Create(TestProvider provider)
    {
        var options = new DbContextOptionsBuilder<DBContext>();

        // A throwaway database per context, so parallel runs and leftovers cannot collide
        var database = $"weequery_test_{Guid.NewGuid():N}";
        var configured = ConfiguredConnectionString(provider) ?? SqlGenerationOnlyConnectionString(provider);

        switch (provider)
        {
            case TestProvider.Sqlite:
                // Pooling=False is required, not a preference: see the remarks on Drop
                options.UseSqlite($"Data Source={Guid.NewGuid()}.db;Pooling=False");
                break;

            case TestProvider.PostgreSql:
                options.UseNpgsql(new NpgsqlConnectionStringBuilder(configured) { Database = database }.ConnectionString);
                break;

            case TestProvider.SqlServer:
                options.UseSqlServer(new SqlConnectionStringBuilder(configured) { InitialCatalog = database }.ConnectionString);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(provider));
        }

        return new DBContext(options.Options);
    }

    /// <summary>
    /// Create the schema and load the shared test set. Caller owns the returned context and should pass it to
    /// <see cref="Drop"/> when finished.
    /// </summary>
    /// <param name="provider"></param>
    /// <returns></returns>
    public static DBContext CreateSeeded(TestProvider provider)
    {
        var context = Create(provider);

        try
        {
            context.Database.EnsureCreated();

            foreach (var minion in MinionTestData.Minions()) { context.Minions.Add(minion); }
            context.SaveChanges();

            return context;
        }
        catch
        {
            Drop(context);
            throw;
        }
    }

    /// <summary>
    /// Delete the database and dispose the context, so a run leaves nothing behind
    /// </summary>
    /// <remarks>
    /// <para>
    /// EnsureDeleted on SQLite calls SqliteConnection.ClearAllPools, which is process wide: dropping this database
    /// reaches into the pooled connections of every other test running at the same time and deactivates them. That
    /// is why the SQLite connection strings here are opened with Pooling=False. Without it roughly one run in
    /// twenty five failed, in whichever test happened to be mid query, as an ObjectDisposedException on
    /// SQLitePCL.sqlite3, a SQLite error 5 "unable to delete/modify user-function due to active statements", or an
    /// IOException deleting a file another pool still held open.
    /// </para>
    /// <para>
    /// A throwaway file per test was never protection against this. The pool is global, so unique names do not keep
    /// the tests apart. Measured at 5 failures in 112 runs before, and 0 in 120 after.
    /// </para>
    /// </remarks>
    /// <param name="context"></param>
    public static void Drop(DBContext context)
    {
        if (context is null) { return; }

        try
        {
            // A test that reached for the raw ADO connection may have left it open, and SQLite cannot delete the
            // file underneath an open handle. CloseConnection is a no-op when EF did not open it itself, so close
            // the underlying connection directly.
            var connection = context.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Closed) { connection.Close(); }

            context.Database.EnsureDeleted();
        }
        finally
        {
            context.Dispose();
        }
    }

    // ---------- reading the SQL that EF generates ----------

    /// <summary>
    /// Each provider prefixes ToQueryString output with its parameter values in its own way: ".param set @x v"
    /// for SQLite, "-- @x='v'" for Npgsql and "DECLARE @x type = v;" for SQL Server. This tells them apart so a
    /// test can look at the statement without the values, or count the parameters.
    /// </summary>
    private static bool IsParameterDeclaration(string line)
    {
        var trimmed = line.TrimStart();

        return trimmed.StartsWith(".param")
            || trimmed.StartsWith("--")
            || trimmed.StartsWith("DECLARE ", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The statement alone, with the parameter preamble stripped. Two queries with the same statement share a
    /// query plan however much their parameter values differ.
    /// </summary>
    /// <param name="queryString">output of ToQueryString</param>
    /// <returns></returns>
    public static string StatementOnly(string queryString)
    {
        return string.Join(
            "\n",
            queryString
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => (line.Length > 0) && (!IsParameterDeclaration(line))));
    }

    /// <summary>
    /// How many parameters the query carries
    /// </summary>
    /// <param name="queryString">output of ToQueryString</param>
    /// <returns></returns>
    public static int ParameterCount(string queryString)
    {
        return queryString
            .Split('\n')
            .Count(line => (line.Trim().Length > 0) && IsParameterDeclaration(line));
    }

    /// <summary>
    /// Run a statement written as SQL by hand, reading values rather than entities.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Several tests exist to check Weequery's SQL against SQL written out by hand, so a raw statement is the
    /// point of them rather than a shortcut. EF1002 warns that building one from an interpolated string risks
    /// injection; it is answered here, once, rather than at each call.
    /// </para>
    /// <para>
    /// What the callers interpolate is the table name, read from the model. An identifier cannot be a parameter,
    /// so there is no parameterized form of it to prefer. Every *value* goes through
    /// <paramref name="parameters"/>, which is the same parameterization any other query gets, and the callers
    /// write "{0}" in the statement for it.
    /// </para>
    /// </remarks>
    /// <typeparam name="TValue">what one column of one row reads as</typeparam>
    /// <param name="context"></param>
    /// <param name="sql">written by the test, with "{0}" where a value goes</param>
    /// <param name="parameters">the values, parameterized</param>
    /// <returns></returns>
    public static IQueryable<TValue> HandWrittenQuery<TValue>(DBContext context, string sql, params object[] parameters)
    {
#pragma warning disable EF1002 // Raw is what the test is for, see the remarks
        return context.Database.SqlQueryRaw<TValue>(sql, parameters);
#pragma warning restore EF1002
    }

    /// <summary>
    /// <inheritdoc cref="HandWrittenQuery" path="/summary"/>
    /// <para>
    /// The same thing reading entities, which is a different EF entry point but the same argument, see the
    /// remarks on <see cref="HandWrittenQuery"/>.
    /// </para>
    /// </summary>
    /// <typeparam name="TEntity"></typeparam>
    /// <param name="context"></param>
    /// <param name="sql">written by the test, with "{0}" where a value goes</param>
    /// <param name="parameters">the values, parameterized</param>
    /// <returns></returns>
    public static IQueryable<TEntity> HandWrittenEntities<TEntity>(DBContext context, string sql, params object[] parameters) where TEntity : class
    {
#pragma warning disable EF1002 // Raw is what the test is for, see the remarks
        return context.Set<TEntity>().FromSqlRaw(sql, parameters);
#pragma warning restore EF1002
    }
}
