using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AIDesignStudio.IntegrationTests;

/// <summary>
/// Boots the real API host against the provisioned test database.
///
/// <para><b>Requests go over <c>https://localhost</c>, deliberately.</b> The
/// session cookie is <c>__Host-</c> prefixed with
/// <c>CookieSecurePolicy.Always</c>, so a browser — and
/// <c>TestServer</c> — will not accept it over plain HTTP. Pointing the test
/// client at an https base address makes <c>Request.IsHttps</c> true so the real
/// cookie policy is exercised. The alternative, relaxing
/// <c>SecurePolicy</c> for tests, would mean the tests passed against a
/// configuration that is not the one shipping.</para>
/// </summary>
internal sealed class StudioApiFactory(
    string connectionString,
    string environment = "Development",
    bool revalidateSessionEveryRequest = false,
    string passwordResetTransport = "DevelopmentLog",
    bool logResetTokens = false,
    Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[]? dbInterceptors = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);

        if (environment != "Development")
        {
            // Outside Development the host refuses to start without a Data
            // Protection key protector (W2-008), which is the behaviour we want.
            // A test that needs a runnable non-Development host therefore has to
            // supply one — exercising the production shape rather than bypassing
            // the check.
            builder.ConfigureServices(services => services
                .AddDataProtection()
                .PersistKeysToDbContext<Infrastructure.Persistence.AppDbContext>()
                .ProtectKeysWithCertificate(TestKeyProtectionCertificate.Value)
                .SetApplicationName("AIDesignStudio"));
        }

        if (dbInterceptors is { Length: > 0 })
        {
            // For tests that have to hold two requests at a known point — the
            // only way to make a race happen on purpose rather than by luck.
            // Added to the host's own registration, so everything else about
            // the context is exactly what ships.
            builder.ConfigureServices(services => Microsoft.Extensions.DependencyInjection
                .EntityFrameworkServiceCollectionExtensions
                .ConfigureDbContext<Infrastructure.Persistence.AppDbContext>(
                    services, options => options.AddInterceptors(dbInterceptors)));
        }

        if (revalidateSessionEveryRequest)
        {
            // Security-stamp validation normally runs every 15 minutes, which no
            // test can wait for. Zero makes it run on every request so revocation
            // is observable (W2-001). The mechanism under test is the validator
            // being attached at all; the interval is a schedule, not the guard.
            builder.ConfigureServices(services => services
                .Configure<Microsoft.AspNetCore.Identity.SecurityStampValidatorOptions>(
                    options => options.ValidationInterval = TimeSpan.Zero));
        }

        // UseSetting, not ConfigureAppConfiguration's in-memory source.
        //
        // This was a real defect, found while wiring M01-02: the in-memory
        // collection registered through ConfigureAppConfiguration did NOT win
        // over the host's own appsettings.json, so the test host connected to the
        // *developer's* database instead of the provisioned test one. Nothing
        // caught it, because the readiness probe only needs some reachable
        // database and the dev one is reachable and migrated. Identity exposed it
        // immediately: auth tables existed in the test database and not in the
        // dev one. UseSetting writes into the host's configuration directly and
        // takes precedence. Asserted by
        // AuthenticationTests.The_test_host_uses_the_provisioned_test_database.
        builder.UseSetting("ConnectionStrings:Default", connectionString);

        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?>
            {

                // A nonexistent executable: readiness must report the provider as
                // unavailable rather than failing the request.
                ["Providers:Stitch:ExecutablePath"] = "stitch-not-installed-in-test",

                // Never enabled in a test. Asserted by
                // AuthenticationTests.The_billable_guard_is_disabled_in_the_test_host.
                ["Providers:Stitch:BillableOperationsEnabled"] = "false",

                // Recovery transport is a startup decision (W2-003), so tests
                // that assert Production behaviour need to set it explicitly.
                ["Auth:PasswordReset:Transport"] = passwordResetTransport,
                ["Auth:PasswordReset:LogTokensInDevelopment"] = logResetTokens ? "true" : "false",
            }));
    }

    /// <summary>A client that keeps cookies and speaks https, i.e. behaves like a browser.</summary>
    public HttpClient CreateBrowserClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        HandleCookies = true,
    });
}

/// <summary>
/// A self-signed certificate used only to satisfy the Data Protection key
/// protector in non-Development test hosts. Generated in-process, never written
/// to disk, and good for an hour.
/// </summary>
internal static class TestKeyProtectionCertificate
{
    public static readonly System.Security.Cryptography.X509Certificates.X509Certificate2 Value
        = Create();

    private static System.Security.Cryptography.X509Certificates.X509Certificate2 Create()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);

        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=aids-tests-key-protection",
            rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);

        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
    }
}

/// <summary>
/// Drives the auth endpoints the way the studio UI will: fetch an antiforgery
/// token, echo it in the header, let the handler carry the session cookie.
/// </summary>
internal sealed class StudioClient(HttpClient http)
{
    public const string AntiforgeryHeader = "X-CSRF-TOKEN";

    public HttpClient Http => http;

    public async Task<string> GetAntiforgeryTokenAsync(CancellationToken cancellationToken)
    {
        var body = await http.GetFromJsonAsync<JsonElement>(
            "/api/v1/auth/antiforgery-token", cancellationToken);

        return body.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("No antiforgery token was issued.");
    }

    /// <summary>POSTs JSON with a freshly fetched antiforgery token attached.</summary>
    public async Task<HttpResponseMessage> PostAsync<T>(
        string path, T payload, CancellationToken cancellationToken)
    {
        var token = await GetAntiforgeryTokenAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload),
        };

        request.Headers.Add(AntiforgeryHeader, token);

        return await http.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Sends any method with a fresh antiforgery token and, when given, a body
    /// of **exactly** this text — for endpoints whose input is a document, where
    /// whitespace, key order and number spelling are part of what is tested.
    /// </summary>
    public async Task<HttpResponseMessage> SendJsonAsync(
        HttpMethod method,
        string path,
        string? json,
        CancellationToken cancellationToken,
        string contentType = "application/json",
        bool antiforgery = true)
    {
        using var request = new HttpRequestMessage(method, path);

        if (json is not null)
        {
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, contentType);
        }

        if (antiforgery)
        {
            request.Headers.Add(AntiforgeryHeader, await GetAntiforgeryTokenAsync(cancellationToken));
        }

        return await http.SendAsync(request, cancellationToken);
    }

    /// <summary>POSTs JSON with **no** antiforgery token, to prove the filter bites.</summary>
    public Task<HttpResponseMessage> PostWithoutAntiforgeryAsync<T>(
        string path, T payload, CancellationToken cancellationToken) =>
        http.PostAsJsonAsync(path, payload, cancellationToken);

    public Task<HttpResponseMessage> RegisterAsync(
        string email, string password, CancellationToken cancellationToken) =>
        PostAsync("/api/v1/auth/register", new { email, password }, cancellationToken);

    public Task<HttpResponseMessage> LoginAsync(
        string email, string password, string? twoFactorCode, CancellationToken cancellationToken) =>
        PostAsync(
            "/api/v1/auth/login",
            new { email, password, twoFactorCode },
            cancellationToken);

    /// <summary>Registers and signs in, returning the session-carrying client.</summary>
    public async Task SignUpAndSignInAsync(
        string email, string password, CancellationToken cancellationToken)
    {
        (await RegisterAsync(email, password, cancellationToken)).EnsureSuccessStatusCode();
        (await LoginAsync(email, password, null, cancellationToken)).EnsureSuccessStatusCode();
    }
}
