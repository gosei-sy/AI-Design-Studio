using AIDesignStudio.Application.Organizations;
using AIDesignStudio.Infrastructure.Organizations;
using AIDesignStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;

namespace AIDesignStudio.IntegrationTests;

/// <summary>
/// Provisions a throwaway PostgreSQL database per test run and applies the EF
/// Core migrations to it.
///
/// The connection string comes from <c>AIDS_TEST_CONNECTION</c>, falling back to
/// the local development server. Deliberately not Testcontainers: that needs a
/// Docker daemon, which is unavailable in some supported development
/// environments, and a test harness that cannot run is worse than one with an
/// explicit prerequisite. CI supplies a PostgreSQL service container through the
/// same variable.
///
/// Each run migrates a fresh database, so "migrations apply cleanly from empty"
/// is verified continuously rather than assumed.
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    private const string EnvVariable = "AIDS_TEST_CONNECTION";

    private const string DefaultAdminConnection =
        "Host=localhost;Port=5432;Database=postgres;Username=aids;Password=aids_dev_only";

    private readonly string _databaseName = $"aids_test_{Guid.CreateVersion7():N}";

    /// <summary>Connection string for the provisioned test database.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>
    /// False when no PostgreSQL server could be reached. Tests report this as a
    /// skip with a clear reason instead of a confusing connection failure.
    /// </summary>
    public bool IsAvailable { get; private set; }

    public string SkipReason { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        var adminConnectionString = AdminConnectionString;

        var builder = new NpgsqlConnectionStringBuilder(adminConnectionString);

        try
        {
            await using var admin = new NpgsqlConnection(adminConnectionString);
            await admin.OpenAsync();

            await using var create = new NpgsqlCommand($"""CREATE DATABASE "{_databaseName}" """, admin);
            await create.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            SkipReason =
                $"No PostgreSQL server reachable at {builder.Host}:{builder.Port} ({ex.GetType().Name}: " +
                $"{ex.Message}). Start one, or set {EnvVariable}.";
            return;
        }

        builder.Database = _databaseName;
        ConnectionString = builder.ConnectionString;

        await using var context = CreateContext();
        await context.Database.MigrateAsync();

        IsAvailable = true;
    }

    /// <summary>
    /// Provisions an extra disposable database, for tests that manipulate schema
    /// state and must not touch the shared one.
    ///
    /// <para>Added for the upgrade-path tests (W3-RR-001, W3-RR-002): both
    /// findings were only reachable by starting at an *older* migration, which
    /// the shared fixture cannot do because it migrates to latest on
    /// initialisation.</para>
    /// </summary>
    public async Task<string> CreateDatabaseAsync(string purpose, CancellationToken cancellationToken)
    {
        var name = $"aids_{purpose}_{Guid.CreateVersion7():N}";
        var builder = new NpgsqlConnectionStringBuilder(AdminConnectionString);

        await using var admin = new NpgsqlConnection(AdminConnectionString);
        await admin.OpenAsync(cancellationToken);

        await using var create = new NpgsqlCommand($"""CREATE DATABASE "{name}" """, admin);
        await create.ExecuteNonQueryAsync(cancellationToken);

        builder.Database = name;

        return builder.ConnectionString;
    }

    /// <summary>
    /// Drops a database created by <see cref="CreateDatabaseAsync"/>. Never
    /// throws: a leaked test database is noise, a failed teardown is a red suite.
    /// </summary>
    public async Task DropDatabaseAsync(string connectionString)
    {
        NpgsqlConnection.ClearAllPools();

        var name = new NpgsqlConnectionStringBuilder(connectionString).Database;
        var admin = new NpgsqlConnectionStringBuilder(AdminConnectionString).ConnectionString;

        try
        {
            await using var connection = new NpgsqlConnection(admin);
            await connection.OpenAsync();

            await using var drop = new NpgsqlCommand(
                $"""DROP DATABASE IF EXISTS "{name}" WITH (FORCE)""", connection);
            await drop.ExecuteNonQueryAsync();
        }
        catch
        {
            // Deliberately swallowed.
        }
    }

    private static string AdminConnectionString =>
        Environment.GetEnvironmentVariable(EnvVariable) ?? DefaultAdminConnection;

    public async ValueTask DisposeAsync()
    {
        if (!IsAvailable)
        {
            return;
        }

        // Clear the pool first: an open connection would block the DROP.
        NpgsqlConnection.ClearAllPools();

        var builder = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = "postgres" };

        try
        {
            await using var admin = new NpgsqlConnection(builder.ConnectionString);
            await admin.OpenAsync();

            await using var drop = new NpgsqlCommand(
                $"""DROP DATABASE IF EXISTS "{_databaseName}" WITH (FORCE)""", admin);
            await drop.ExecuteNonQueryAsync();
        }
        catch
        {
            // A leaked test database is noise, not a failure. Never fail teardown.
        }
    }

    /// <summary>
    /// Builds a context over the provisioned test database.
    /// </summary>
    /// <param name="interceptors">
    /// Optional command interceptors. Used to land deterministically inside the
    /// window between a read and its write — see <see cref="LeaseExpiryInterceptor"/>.
    /// </param>
    public AppDbContext CreateContext(params IInterceptor[] interceptors) =>
        CreateScopedContext(SystemOrganizationScope.Instance, interceptors);

    /// <summary>
    /// Builds a context with an explicit organization scope, for the tests that
    /// exist to prove the scope works.
    ///
    /// <para>The default above is <b>system</b> scope, and that choice is
    /// test-harness-only. A context built with no scope at all sees no
    /// organization-scoped row — which is the right production default and the
    /// wrong one for a fixture whose job is to set up and inspect arbitrary data.
    /// Making it explicit here keeps the elevation in one visible place instead
    /// of spreading <c>IgnoreQueryFilters()</c> through fifty assertions.</para>
    /// </summary>
    public AppDbContext CreateScopedContext(
        IOrganizationScope scope, params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ConnectionString, npgsql => npgsql
                .MigrationsHistoryTable("__ef_migrations_history", "app"))
            .UseSnakeCaseNamingConvention();

        if (interceptors.Length > 0)
        {
            options.AddInterceptors(interceptors);
        }

        // Set AIDS_TEST_SQL_LOG=1 to see the generated SQL when diagnosing a
        // persistence failure.
        if (Environment.GetEnvironmentVariable("AIDS_TEST_SQL_LOG") == "1")
        {
            options.LogTo(Console.WriteLine, Microsoft.Extensions.Logging.LogLevel.Information)
                .EnableSensitiveDataLogging();
        }

        return new AppDbContext(options.Options, scope);
    }
}

[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "postgres";
}
