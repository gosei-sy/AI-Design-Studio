using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace AIDesignStudio.IntegrationTests;

/// <summary>
/// Boots the real API host in-process and exercises its endpoints over HTTP.
///
/// The host is configured to point at the provisioned test database, so the
/// readiness probe is a genuine end-to-end check: configuration, dependency
/// injection, EF Core model building and a live connection all have to work.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ApiEndpointTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    private HttpClient Client => _client
        ?? throw new InvalidOperationException("The test host was not initialised.");

    public ValueTask InitializeAsync()
    {
        if (!fixture.IsAvailable)
        {
            return ValueTask.CompletedTask;
        }

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");

            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = fixture.ConnectionString,

                    // A nonexistent executable: the readiness probe must report the
                    // provider as unavailable rather than failing the request.
                    ["Providers:Stitch:ExecutablePath"] = "stitch-not-installed-in-test",
                    ["Providers:Stitch:BillableOperationsEnabled"] = "false",
                }));
        });

        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();

        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    private void RequireDatabase() => Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);

    // -----------------------------------------------------------------------
    // Health
    // -----------------------------------------------------------------------

    [Fact]
    public async Task The_minimal_health_probe_returns_ok()
    {
        RequireDatabase();

        var response = await Client.GetAsync("/health", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);

        body.GetProperty("status").GetString().ShouldBe("ok");
    }

    /// <summary>
    /// Liveness must not depend on the database: a database outage should not
    /// cause an orchestrator to restart an otherwise healthy process.
    /// </summary>
    [Fact]
    public async Task The_liveness_probe_passes_and_excludes_the_database()
    {
        RequireDatabase();

        var response = await Client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);

        body.GetProperty("status").GetString().ShouldBe("Healthy");

        var checks = body.GetProperty("checks").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString())
            .ToList();

        checks.ShouldContain("self");
        checks.ShouldNotContain("database");
    }

    [Fact]
    public async Task The_readiness_probe_passes_and_includes_a_real_database_check()
    {
        RequireDatabase();

        var response = await Client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);

        body.GetProperty("status").GetString().ShouldBe("Healthy");

        var checks = body.GetProperty("checks").EnumerateArray().ToList();
        var database = checks.ShouldHaveSingleItem();

        database.GetProperty("name").GetString().ShouldBe("database");
        database.GetProperty("status").GetString().ShouldBe("Healthy");
    }

    /// <summary>
    /// Health output is typically unauthenticated, so it must not leak a
    /// connection string, host name or credential.
    /// </summary>
    [Fact]
    public async Task The_readiness_probe_does_not_leak_connection_details()
    {
        RequireDatabase();

        var body = await Client.GetStringAsync("/health/ready", TestContext.Current.CancellationToken);

        body.ShouldNotContain("Password", Case.Insensitive);
        body.ShouldNotContain("aids_dev_only");
        body.ShouldNotContain("Host=");
    }

    // -----------------------------------------------------------------------
    // Provider diagnostics
    // -----------------------------------------------------------------------

    [Fact]
    public async Task The_provider_list_reports_the_stitch_adapter_and_its_capabilities()
    {
        RequireDatabase();

        var body = await Client.GetFromJsonAsync<JsonElement>(
            "/api/v1/providers", TestContext.Current.CancellationToken);

        var provider = body.EnumerateArray().ShouldHaveSingleItem();
        provider.GetProperty("providerId").GetString().ShouldBe("google-stitch");

        var capabilities = provider.GetProperty("capabilities").EnumerateArray()
            .Select(c => c.GetString())
            .ToList();

        capabilities.ShouldContain("IScreenGenerationCapability");
        capabilities.ShouldContain("IScreenImportCapability");
        capabilities.ShouldContain("IDesignSystemProjectionCapability");

        // Not verified against a live account during M00, so not claimed.
        capabilities.ShouldNotContain("IScreenVariantCapability");
    }

    /// <summary>
    /// With the CLI absent, readiness must report the gap as data rather than
    /// throwing: an unconfigured provider is an expected state, not a server error.
    /// </summary>
    [Fact]
    public async Task Provider_readiness_reports_a_missing_cli_as_data_not_as_an_error()
    {
        RequireDatabase();

        var response = await Client.GetAsync(
            "/api/v1/providers/google-stitch/readiness", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);

        body.GetProperty("isInstalled").GetBoolean().ShouldBeFalse();
        body.GetProperty("isUsable").GetBoolean().ShouldBeFalse();
        body.GetProperty("detail").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task An_unknown_provider_returns_not_found()
    {
        RequireDatabase();

        var response = await Client.GetAsync(
            "/api/v1/providers/figma/readiness", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_openapi_document_is_served_in_development()
    {
        RequireDatabase();

        var response = await Client.GetAsync(
            "/swagger/v1/swagger.json", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var document = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);

        var paths = document.GetProperty("paths").EnumerateObject()
            .Select(p => p.Name)
            .ToList();

        paths.ShouldContain(p => p.StartsWith("/api/v1/providers", StringComparison.Ordinal));
        paths.ShouldContain("/health");
    }
}
