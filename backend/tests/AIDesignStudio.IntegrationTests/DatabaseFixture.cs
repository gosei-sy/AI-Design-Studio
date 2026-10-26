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
        var adminConnectionString =
            Environment.GetEnvironmentVariable(EnvVariable) ?? DefaultAdminConnection;

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
    public AppDbContext CreateContext(params IInterceptor[] interceptors)
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

        return new AppDbContext(options.Options);
    }
}

[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "postgres";
}
