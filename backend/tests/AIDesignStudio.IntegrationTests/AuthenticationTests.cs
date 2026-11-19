using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AIDesignStudio.Api.Identity;
using AIDesignStudio.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace AIDesignStudio.IntegrationTests;

/// <summary>
/// M01-02 — authentication, against the real host and a real PostgreSQL
/// database. Decision D-7 / Option A; see ADR-0008.
///
/// These are integration tests rather than unit tests because every property
/// worth asserting here is a property of the composition: whether the fallback
/// policy actually closes an endpoint, whether the cookie is really HttpOnly,
/// whether lockout survives a round trip to the database. A mocked
/// <c>UserManager</c> would prove none of it.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AuthenticationTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private const string Password = "Correct-Horse-9-Battery!";

    private StudioApiFactory? _factory;

    private StudioApiFactory Factory => _factory
        ?? throw new InvalidOperationException("The test host was not initialised.");

    public ValueTask InitializeAsync()
    {
        if (fixture.IsAvailable)
        {
            _factory = new StudioApiFactory(fixture.ConnectionString);
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    private void RequireDatabase() => Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);

    private StudioClient NewClient() => new(Factory.CreateBrowserClient());

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.CreateVersion7():N}@example.test";

    /// <summary>
    /// Creates an account **without** the registration endpoint.
    ///
    /// <para>Needed because M01-03 unmapped <c>POST /auth/register</c> outside
    /// Development (D-9, ADR-0009), so the three tests below — which exist to
    /// assert non-Development behaviour — can no longer register their own
    /// fixture. Using the user store directly keeps each test asserting the
    /// property it was written for (response uniformity, lockout invisibility)
    /// rather than quietly becoming a test of how accounts are created.</para>
    /// </summary>
    private async Task CreateAccountAsync(StudioApiFactory factory, string email)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var result = await users.CreateAsync(new ApplicationUser(email), Password);

        result.Succeeded.ShouldBeTrue(
            string.Join(" ", result.Errors.Select(e => e.Description)));
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The response body with <c>traceId</c> removed.
    ///
    /// Two responses that must be indistinguishable to an attacker still carry
    /// different trace ids, because a trace id is per-request and is exactly the
    /// thing an operator needs to correlate a report. Comparing raw bodies would
    /// make every uniformity assertion fail for a reason that is not a leak.
    /// </summary>
    private async Task<string> ComparableBodyAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        using var document = System.Text.Json.JsonDocument.Parse(body);

        var fields = document.RootElement.EnumerateObject()
            .Where(p => p.Name != "traceId")
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => $"{p.Name}={p.Value}");

        return string.Join("&", fields);
    }

    // -----------------------------------------------------------------------
    // S-19 — authentication is required, and closed by default
    // -----------------------------------------------------------------------

    /// <summary>
    /// The acceptance criterion: a non-health endpoint refuses an
    /// unauthenticated caller with 401 rather than serving data.
    /// </summary>
    [Theory]
    [InlineData("/api/v1/providers")]
    [InlineData("/api/v1/providers/google-stitch/readiness")]
    [InlineData("/api/v1/auth/me")]
    public async Task An_unauthenticated_request_to_a_protected_endpoint_is_refused(string path)
    {
        RequireDatabase();

        var response = await NewClient().Http.GetAsync(path, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// A 401, not a 302. An API that redirects an unauthenticated fetch() to a
    /// login page hands the caller a 200 full of HTML, which is worse than an
    /// error because it looks like success.
    /// </summary>
    [Fact]
    public async Task An_unauthenticated_request_is_not_redirected_to_a_login_page()
    {
        RequireDatabase();

        var response = await NewClient().Http.GetAsync("/api/v1/providers", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.Location.ShouldBeNull();
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_endpoints_remain_anonymous(string path)
    {
        RequireDatabase();

        var response = await NewClient().Http.GetAsync(path, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_authenticated_caller_reaches_a_protected_endpoint()
    {
        RequireDatabase();

        var client = NewClient();
        await client.SignUpAndSignInAsync(UniqueEmail("reader"), Password, Ct);

        var response = await client.Http.GetAsync("/api/v1/providers", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // -----------------------------------------------------------------------
    // S-21 — an approval is attributable to a real identity
    // -----------------------------------------------------------------------

    /// <summary>
    /// The identity the domain will stamp into <c>ApprovedBy</c> / <c>WaivedBy</c>
    /// has to be a real one that reached it through <c>ICurrentUser</c>.
    /// </summary>
    [Fact]
    public async Task The_authenticated_identity_is_available_for_domain_audit_fields()
    {
        RequireDatabase();

        var email = UniqueEmail("approver");
        var client = NewClient();
        await client.SignUpAndSignInAsync(email, Password, Ct);

        var body = await client.Http.GetFromJsonAsync<JsonElement>("/api/v1/auth/me", Ct);

        body.GetProperty("email").GetString().ShouldBe(email);
        body.GetProperty("userId").GetString().ShouldNotBeNullOrWhiteSpace();

        // The audit subject is the immutable id, not the self-asserted address
        // (W2-005). Asserted in detail by
        // The_audit_identity_is_the_immutable_user_id_not_the_email.
        body.GetProperty("auditIdentity").GetString()
            .ShouldBe(body.GetProperty("userId").GetString());
    }

    // -----------------------------------------------------------------------
    // The session cookie
    // -----------------------------------------------------------------------

    /// <summary>
    /// The browser must never be able to read the session. This is the whole
    /// reason Option A was chosen over a bearer token: an XSS cannot exfiltrate
    /// a cookie it cannot see.
    /// </summary>
    [Fact]
    public async Task The_session_cookie_is_http_only_secure_and_same_site_strict()
    {
        RequireDatabase();

        var client = NewClient();
        var email = UniqueEmail("cookie");

        (await client.RegisterAsync(email, Password, Ct)).EnsureSuccessStatusCode();

        var login = await client.LoginAsync(email, Password, null, Ct);
        login.EnsureSuccessStatusCode();

        var setCookie = login.Headers
            .GetValues("Set-Cookie")
            .FirstOrDefault(c => c.StartsWith(SessionAuthentication.SessionCookieName, StringComparison.Ordinal));

        setCookie.ShouldNotBeNull("the login response must issue the session cookie");
        setCookie.ShouldContain("httponly", Case.Insensitive);
        setCookie.ShouldContain("secure", Case.Insensitive);
        setCookie.ShouldContain("samesite=strict", Case.Insensitive);

        // The __Host- prefix is only honoured by browsers when path=/ and no
        // Domain attribute is present, so assert both rather than trusting the
        // name alone.
        setCookie.ShouldContain("path=/", Case.Insensitive);
        setCookie.ShouldNotContain("domain=", Case.Insensitive);
    }

    [Fact]
    public async Task No_session_token_appears_in_any_response_body()
    {
        RequireDatabase();

        var client = NewClient();
        var email = UniqueEmail("nobody");

        (await client.RegisterAsync(email, Password, Ct)).EnsureSuccessStatusCode();

        var login = await client.LoginAsync(email, Password, null, Ct);
        var body = await login.Content.ReadAsStringAsync(Ct);

        // A bearer-token design would put a credential here. Option A must not.
        body.ShouldNotContain("token", Case.Insensitive);
        body.ShouldNotContain(Password);
    }

    [Fact]
    public async Task Logout_clears_the_session_and_protected_endpoints_refuse_again()
    {
        RequireDatabase();

        var client = NewClient();
        await client.SignUpAndSignInAsync(UniqueEmail("leaver"), Password, Ct);

        (await client.Http.GetAsync("/api/v1/auth/me", Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        (await client.PostAsync("/api/v1/auth/logout", new { }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await client.Http.GetAsync("/api/v1/auth/me", Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // -----------------------------------------------------------------------
    // Antiforgery — the obligation Option A carries
    // -----------------------------------------------------------------------

    /// <summary>
    /// A JSON minimal-API endpoint is NOT validated by the antiforgery
    /// middleware — it only validates form-bound endpoints. Without the explicit
    /// filter, a cookie-session JSON API has no CSRF protection at all, so this
    /// test is the one that proves the filter is actually wired.
    /// </summary>
    [Fact]
    public async Task A_state_changing_request_without_an_antiforgery_token_is_rejected()
    {
        RequireDatabase();

        var client = NewClient();
        await client.SignUpAndSignInAsync(UniqueEmail("csrf"), Password, Ct);

        var response = await client.PostWithoutAntiforgeryAsync(
            "/api/v1/auth/change-password",
            new { currentPassword = Password, newPassword = "Another-Horse-9-Battery!" },
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("code").GetString().ShouldBe("auth.antiforgery_failed");
    }

    [Fact]
    public async Task A_safe_request_needs_no_antiforgery_token()
    {
        RequireDatabase();

        var client = NewClient();
        await client.SignUpAndSignInAsync(UniqueEmail("safe"), Password, Ct);

        (await client.Http.GetAsync("/api/v1/auth/me", Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // -----------------------------------------------------------------------
    // Account enumeration
    // -----------------------------------------------------------------------

    /// <summary>
    /// Registering an address that already exists must look exactly like
    /// registering a new one. Otherwise the endpoint is an account directory.
    /// </summary>
    [Fact]
    public async Task Registering_an_existing_address_is_indistinguishable_from_a_new_one()
    {
        RequireDatabase();

        var client = NewClient();
        var email = UniqueEmail("dup");

        var first = await client.RegisterAsync(email, Password, Ct);
        var second = await client.RegisterAsync(email, Password, Ct);

        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        second.StatusCode.ShouldBe(first.StatusCode);

        (await second.Content.ReadAsStringAsync(Ct))
            .ShouldBe(await first.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>
    /// An unknown address and a wrong password must produce the same response,
    /// down to the error code.
    /// </summary>
    [Fact]
    public async Task An_unknown_address_and_a_wrong_password_fail_identically()
    {
        RequireDatabase();

        var client = NewClient();
        var email = UniqueEmail("known");

        (await client.RegisterAsync(email, Password, Ct)).EnsureSuccessStatusCode();

        var wrongPassword = await client.LoginAsync(email, "Wrong-Horse-9-Battery!", null, Ct);
        var unknownAddress = await client.LoginAsync(UniqueEmail("ghost"), Password, null, Ct);

        wrongPassword.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        unknownAddress.StatusCode.ShouldBe(wrongPassword.StatusCode);

        var a = await wrongPassword.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var b = await unknownAddress.Content.ReadFromJsonAsync<JsonElement>(Ct);

        a.GetProperty("code").GetString().ShouldBe("auth.invalid_credentials");
        b.GetProperty("code").GetString().ShouldBe(a.GetProperty("code").GetString());
        b.GetProperty("detail").GetString().ShouldBe(a.GetProperty("detail").GetString());
    }

    /// <summary>
    /// A reset request for an address that does not exist must look like one for
    /// an address that does — and neither may contain the token.
    /// </summary>
    [Fact]
    public async Task A_password_reset_request_never_reveals_whether_the_address_exists()
    {
        RequireDatabase();

        var client = NewClient();
        var known = UniqueEmail("resettable");

        (await client.RegisterAsync(known, Password, Ct)).EnsureSuccessStatusCode();

        var forKnown = await client.PostAsync(
            "/api/v1/auth/forgot-password", new { email = known }, Ct);
        var forUnknown = await client.PostAsync(
            "/api/v1/auth/forgot-password", new { email = UniqueEmail("ghost") }, Ct);

        forKnown.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        forUnknown.StatusCode.ShouldBe(forKnown.StatusCode);

        var knownBody = await forKnown.Content.ReadAsStringAsync(Ct);
        knownBody.ShouldBe(await forUnknown.Content.ReadAsStringAsync(Ct));

        // The token travels through IPasswordResetNotifier, never the response.
        knownBody.ShouldNotContain("token", Case.Insensitive);
    }

    // -----------------------------------------------------------------------
    // Password lifecycle
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_password_below_policy_is_refused_at_registration()
    {
        RequireDatabase();

        var response = await NewClient().RegisterAsync(UniqueEmail("weak"), "short", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("code").GetString().ShouldBe("auth.registration_failed");
    }

    [Fact]
    public async Task Changing_the_password_invalidates_the_old_one_and_keeps_the_session()
    {
        RequireDatabase();

        var client = NewClient();
        var email = UniqueEmail("rotate");
        const string newPassword = "Rotated-Horse-9-Battery!";

        await client.SignUpAndSignInAsync(email, Password, Ct);

        (await client.PostAsync(
                "/api/v1/auth/change-password",
                new { currentPassword = Password, newPassword },
                Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // RefreshSignInAsync keeps the caller signed in through the rotation.
        (await client.Http.GetAsync("/api/v1/auth/me", Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var fresh = NewClient();
        (await fresh.LoginAsync(email, Password, null, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await fresh.LoginAsync(email, newPassword, null, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// The full recovery path, driven through the notifier port rather than a
    /// mailbox — which is also the only way to assert the token never appears in
    /// a response.
    /// </summary>
    [Fact]
    public async Task A_forgotten_password_can_be_reset_with_the_token_from_the_notifier()
    {
        RequireDatabase();

        var email = UniqueEmail("recover");
        const string newPassword = "Recovered-Horse-9-Battery!";

        var client = NewClient();
        (await client.RegisterAsync(email, Password, Ct)).EnsureSuccessStatusCode();

        // Mint the token the way the endpoint does, through the real store.
        string token;
        using (var scope = Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByEmailAsync(email);
            user.ShouldNotBeNull();
            token = await users.GeneratePasswordResetTokenAsync(user!);
        }

        (await client.PostAsync(
                "/api/v1/auth/reset-password",
                new { email, token, newPassword },
                Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var fresh = NewClient();
        (await fresh.LoginAsync(email, newPassword, null, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await fresh.LoginAsync(email, Password, null, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_reset_with_an_invalid_token_is_refused()
    {
        RequireDatabase();

        var client = NewClient();
        var email = UniqueEmail("badtoken");

        (await client.RegisterAsync(email, Password, Ct)).EnsureSuccessStatusCode();

        var response = await client.PostAsync(
            "/api/v1/auth/reset-password",
            new { email, token = "not-a-real-token", newPassword = "Nope-Horse-9-Battery!" },
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("code").GetString().ShouldBe("auth.reset_failed");
    }

    // -----------------------------------------------------------------------
    // Lockout
    // -----------------------------------------------------------------------

    /// <summary>
    /// Five failures then lockout, and the lockout is reported as 423 rather
    /// than hidden behind the generic credential error — the account owner needs
    /// to know why a correct password stopped working.
    /// </summary>
    [Fact]
    public async Task Repeated_failures_lock_the_account_and_the_correct_password_then_fails()
    {
        RequireDatabase();

        var client = NewClient();
        var email = UniqueEmail("lockout");

        (await client.RegisterAsync(email, Password, Ct)).EnsureSuccessStatusCode();

        // MaxFailedAccessAttempts = 5, and the fifth failure both trips the lock
        // and reports it — so attempts 1–4 are credential errors and the fifth is
        // already 423. The first version of this test expected the lock on a
        // sixth attempt; the implementation was right and the expectation wrong.
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            (await client.LoginAsync(email, "Wrong-Horse-9-Battery!", null, Ct))
                .StatusCode.ShouldBe(
                    HttpStatusCode.Unauthorized,
                    $"attempt {attempt} of 4 should fail as a credential error, not a lockout");
        }

        (await client.LoginAsync(email, "Wrong-Horse-9-Battery!", null, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "the fifth failure trips the lock");

        // The *correct* password is refused while locked, which is the property
        // that makes lockout worth anything at all.
        //
        // Reported as 401, not 423: W2-002 established that a distinct lockout
        // response is an account-existence oracle. Lockout_is_not_distinguishable_from_an_unknown_account
        // asserts the suppression, and checks the store to prove the protection
        // still took effect.
        (await client.LoginAsync(email, Password, null, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // -----------------------------------------------------------------------
    // Two-factor authentication
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Two_factor_enrollment_then_login_requires_a_code()
    {
        RequireDatabase();

        var email = UniqueEmail("mfa");
        var client = NewClient();
        await client.SignUpAndSignInAsync(email, Password, Ct);

        var enroll = await client.PostAsync("/api/v1/auth/two-factor/enroll", new { }, Ct);
        enroll.StatusCode.ShouldBe(HttpStatusCode.OK);

        var enrollBody = await enroll.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var sharedKey = enrollBody.GetProperty("sharedKey").GetString();
        sharedKey.ShouldNotBeNullOrWhiteSpace();
        enrollBody.GetProperty("isEnabled").GetBoolean().ShouldBeFalse(
            "enrolling must not enable the factor before possession is proven");

        // Enrolment always mints a fresh secret, so a key handed out earlier
        // cannot be enabled later by someone who kept it (W2-004).

        // A wrong code must not enable it.
        (await client.PostAsync("/api/v1/auth/two-factor/enable", new { code = "000000" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var code = await GenerateAuthenticatorCodeAsync(email);

        (await client.PostAsync("/api/v1/auth/two-factor/enable", new { code }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // A fresh sign-in now needs the second factor: the password alone must
        // not produce a session.
        var fresh = NewClient();
        var passwordOnly = await fresh.LoginAsync(email, Password, null, Ct);

        passwordOnly.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await passwordOnly.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("requiresTwoFactor").GetBoolean().ShouldBeTrue();
        body.GetProperty("authenticated").GetBoolean().ShouldBeFalse();

        // And the half-finished login granted nothing.
        (await fresh.Http.GetAsync("/api/v1/auth/me", Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // With a code, it succeeds.
        var withCode = await fresh.LoginAsync(
            email, Password, await GenerateAuthenticatorCodeAsync(email), Ct);

        withCode.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await withCode.Content.ReadFromJsonAsync<JsonElement>(Ct))
            .GetProperty("authenticated").GetBoolean().ShouldBeTrue();
    }

    /// <summary>
    /// Disabling the second factor needs a current code. Otherwise a stolen
    /// session could strip the very protection it exists to provide.
    /// </summary>
    [Fact]
    public async Task Disabling_two_factor_requires_a_valid_code()
    {
        RequireDatabase();

        var email = UniqueEmail("mfa-off");
        var client = NewClient();
        await client.SignUpAndSignInAsync(email, Password, Ct);

        (await client.PostAsync("/api/v1/auth/two-factor/enroll", new { }, Ct))
            .EnsureSuccessStatusCode();
        (await client.PostAsync(
                "/api/v1/auth/two-factor/enable",
                new { code = await GenerateAuthenticatorCodeAsync(email) },
                Ct))
            .EnsureSuccessStatusCode();

        (await client.PostAsync(
                "/api/v1/auth/two-factor/disable",
                new { currentPassword = Password, code = "000000" },
                Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await client.PostAsync(
                "/api/v1/auth/two-factor/disable",
                new { currentPassword = Password, code = await GenerateAuthenticatorCodeAsync(email) },
                Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    /// <summary>
    /// Produces a real TOTP code from the user's stored shared key.
    ///
    /// Computed by <see cref="Totp"/> rather than by
    /// <c>UserManager.GenerateTwoFactorTokenAsync</c>, because the authenticator
    /// provider's GenerateAsync returns an empty string by design — generating
    /// the code belongs to the app, not the server. The first version of these
    /// tests used the UserManager and failed with a valid-looking empty code.
    /// </summary>
    private async Task<string> GenerateAuthenticatorCodeAsync(string email)
    {
        using var scope = Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = await users.FindByEmailAsync(email);
        user.ShouldNotBeNull();

        var key = await users.GetAuthenticatorKeyAsync(user!);
        key.ShouldNotBeNullOrWhiteSpace("the user must be enrolled before a code can be computed");

        return Totp.Generate(key!);
    }

    // -----------------------------------------------------------------------
    // W2-001 — security-stamp revocation
    // -----------------------------------------------------------------------

    /// <summary>
    /// The regression the first round could not catch. Two sessions exist; one
    /// changes the password; the other must stop working.
    ///
    /// The original test only proved the *old password* could not create a new
    /// session and that the caller's own refreshed session survived. It never
    /// created a second pre-existing session, so it could not notice that
    /// `OnValidatePrincipal` was never attached and nothing was ever revoked.
    /// </summary>
    [Fact]
    public async Task Changing_the_password_revokes_other_sessions()
    {
        RequireDatabase();

        await using var factory = new StudioApiFactory(
            fixture.ConnectionString, revalidateSessionEveryRequest: true);

        var email = UniqueEmail("revoke");
        const string newPassword = "Revoked-Horse-9-Battery!";

        var first = new StudioClient(factory.CreateBrowserClient());
        await first.SignUpAndSignInAsync(email, Password, Ct);

        var second = new StudioClient(factory.CreateBrowserClient());
        (await second.LoginAsync(email, Password, null, Ct)).EnsureSuccessStatusCode();

        // Both sessions work.
        (await first.Http.GetAsync("/api/v1/auth/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await second.Http.GetAsync("/api/v1/auth/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await first.PostAsync(
                "/api/v1/auth/change-password",
                new { currentPassword = Password, newPassword },
                Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // The other session is revoked...
        (await second.Http.GetAsync("/api/v1/auth/me", Ct))
            .StatusCode.ShouldBe(
                HttpStatusCode.Unauthorized,
                "a password change must invalidate sessions the caller does not hold");

        // ...and the caller's own session survives, because it was refreshed.
        (await first.Http.GetAsync("/api/v1/auth/me", Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Resetting_the_password_revokes_existing_sessions()
    {
        RequireDatabase();

        await using var factory = new StudioApiFactory(
            fixture.ConnectionString, revalidateSessionEveryRequest: true);

        var email = UniqueEmail("reset-revoke");
        const string newPassword = "ResetRevoked-Horse-9!";

        var session = new StudioClient(factory.CreateBrowserClient());
        await session.SignUpAndSignInAsync(email, Password, Ct);
        (await session.Http.GetAsync("/api/v1/auth/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        string token;
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByEmailAsync(email);
            token = await users.GeneratePasswordResetTokenAsync(user!);
        }

        var anonymous = new StudioClient(factory.CreateBrowserClient());
        (await anonymous.PostAsync(
                "/api/v1/auth/reset-password",
                new { email, token, newPassword },
                Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await session.Http.GetAsync("/api/v1/auth/me", Ct))
            .StatusCode.ShouldBe(
                HttpStatusCode.Unauthorized, "a reset must invalidate live sessions");
    }

    // -----------------------------------------------------------------------
    // W2-002 — the three enumeration oracles, in Production
    // -----------------------------------------------------------------------

    /// <summary>
    /// A weak password must be refused identically whether or not the address
    /// exists. The oracle was: weak + existing → 202, weak + new → 400.
    /// </summary>
    [Fact]
    public async Task A_weak_password_is_refused_identically_for_existing_and_unknown_addresses()
    {
        RequireDatabase();

        // Development, because this asserts a property of the **registration
        // endpoint** and M01-03 unmapped that endpoint outside Development (D-9,
        // ADR-0009). Running it against Staging would now assert nothing but the
        // route's absence, which Public_registration_is_absent_outside_development
        // covers directly.
        var client = NewClient();

        var existing = UniqueEmail("weak-existing");
        (await client.RegisterAsync(existing, Password, Ct)).EnsureSuccessStatusCode();

        var forExisting = await client.RegisterAsync(existing, "weak", Ct);
        var forUnknown = await client.RegisterAsync(UniqueEmail("weak-unknown"), "weak", Ct);

        forExisting.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        forUnknown.StatusCode.ShouldBe(forExisting.StatusCode);

        (await ComparableBodyAsync(forExisting))
            .ShouldBe(await ComparableBodyAsync(forUnknown));
    }

    /// <summary>
    /// Lockout must not be observable by an anonymous caller. The oracle was:
    /// fifth wrong attempt on a real account → 423, unknown account → 401.
    /// </summary>
    [Fact]
    public async Task Lockout_is_not_distinguishable_from_an_unknown_account()
    {
        RequireDatabase();

        await using var factory = new StudioApiFactory(fixture.ConnectionString, "Staging",
            passwordResetTransport: "None");
        var client = new StudioClient(factory.CreateBrowserClient());

        var email = UniqueEmail("silent-lockout");
        await CreateAccountAsync(factory, email);

        HttpResponseMessage? last = null;

        for (var attempt = 1; attempt <= 6; attempt++)
        {
            last = await client.LoginAsync(email, "Wrong-Horse-9-Battery!", null, Ct);
            last.StatusCode.ShouldBe(
                HttpStatusCode.Unauthorized,
                $"attempt {attempt} must not reveal lockout state");
        }

        var unknown = await client.LoginAsync(UniqueEmail("never"), Password, null, Ct);
        unknown.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await ComparableBodyAsync(last!))
            .ShouldBe(await ComparableBodyAsync(unknown));

        // The lockout still *happened* — it is just invisible from outside.
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByEmailAsync(email);
        (await users.IsLockedOutAsync(user!)).ShouldBeTrue(
            "suppressing the response must not suppress the protection");
    }

    /// <summary>
    /// Recovery must respond identically in Production with no transport. The
    /// oracle was: known address → 500 (the notifier threw), unknown → 202.
    /// </summary>
    [Fact]
    public async Task Password_recovery_responds_identically_outside_development_without_a_transport()
    {
        RequireDatabase();

        await using var factory = new StudioApiFactory(fixture.ConnectionString, "Staging",
            passwordResetTransport: "None");
        var client = new StudioClient(factory.CreateBrowserClient());

        var known = UniqueEmail("prod-recover");
        await CreateAccountAsync(factory, known);

        var forKnown = await client.PostAsync(
            "/api/v1/auth/forgot-password", new { email = known }, Ct);
        var forUnknown = await client.PostAsync(
            "/api/v1/auth/forgot-password", new { email = UniqueEmail("prod-ghost") }, Ct);

        forKnown.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        forUnknown.StatusCode.ShouldBe(forKnown.StatusCode);
        (await forKnown.Content.ReadAsStringAsync(Ct))
            .ShouldBe(await forUnknown.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>
    /// W2-003 / W2-008 fail-closed. Production refuses to start without a
    /// recovery transport, so the unavailable-recovery state cannot reach a user
    /// at all — which is a stronger guarantee than making the response uniform.
    /// </summary>
    [Fact]
    public async Task Production_refuses_to_start_without_a_password_reset_transport()
    {
        RequireDatabase();

        await using var factory = new StudioApiFactory(
            fixture.ConnectionString, "Production", passwordResetTransport: "None");

        var failure = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var client = factory.CreateBrowserClient();
            await client.GetAsync("/health", Ct);
        });

        failure.Message.ShouldContain("Password recovery has no delivery transport");
        failure.Message.ShouldContain("D-8");
    }

    /// <summary>
    /// The **full** environment × transport × log-flag matrix: 3 × 3 × 2 = 18.
    ///
    /// The previous round called eight hand-picked cases a matrix and concluded
    /// the gate was sound. It was not a matrix, and the review said so
    /// (M01-W2-R3). Enumerating the product is the point — a gate with a hole in
    /// it looks identical to a gate without one until every combination is
    /// actually executed.
    ///
    /// The rule being asserted: the host starts only when the transport is
    /// permitted for the environment. `Email` is never permitted because it is
    /// unimplemented; `DevelopmentLog` only in Development; `None` anywhere but
    /// Production. The log-flag never affects startup — it affects whether a
    /// token may be written, which
    /// <c>PasswordResetNotifierTests</c> covers directly.
    /// </summary>
    [Theory]
    // Development — everything except Email is allowed.
    [InlineData("Development", "None", false, true)]
    [InlineData("Development", "None", true, true)]
    [InlineData("Development", "DevelopmentLog", false, true)]
    [InlineData("Development", "DevelopmentLog", true, true)]
    [InlineData("Development", "Email", false, false)]
    [InlineData("Development", "Email", true, false)]
    // Staging — None is allowed (with a warning); a dev transport is not.
    [InlineData("Staging", "None", false, true)]
    [InlineData("Staging", "None", true, true)]
    [InlineData("Staging", "DevelopmentLog", false, false)]
    [InlineData("Staging", "DevelopmentLog", true, false)]
    [InlineData("Staging", "Email", false, false)]
    [InlineData("Staging", "Email", true, false)]
    // Production — nothing available today is acceptable.
    [InlineData("Production", "None", false, false)]
    [InlineData("Production", "None", true, false)]
    [InlineData("Production", "DevelopmentLog", false, false)]
    [InlineData("Production", "DevelopmentLog", true, false)]
    [InlineData("Production", "Email", false, false)]
    [InlineData("Production", "Email", true, false)]
    public async Task The_reset_transport_matrix_starts_only_where_it_is_safe(
        string environment, string transport, bool logTokens, bool startupShouldSucceed)
    {
        RequireDatabase();

        await using var factory = new StudioApiFactory(
            fixture.ConnectionString, environment,
            passwordResetTransport: transport,
            logResetTokens: logTokens);

        if (startupShouldSucceed)
        {
            using var client = factory.CreateBrowserClient();
            (await client.GetAsync("/health", Ct)).StatusCode.ShouldBe(
                HttpStatusCode.OK,
                $"{environment} + {transport} (logTokens={logTokens}) should start");
            return;
        }

        var failure = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var client = factory.CreateBrowserClient();
            await client.GetAsync("/health", Ct);
        });

        if (transport == "DevelopmentLog")
        {
            failure.Message.ShouldContain("permitted only in Development");
        }
        else if (transport == "Email")
        {
            failure.Message.ShouldContain("no email transport is implemented");
        }
        else
        {
            failure.Message.ShouldContain("no delivery transport");
        }
    }

    /// <summary>
    /// A transport that is configured but unimplemented is also a startup
    /// failure, not a silent downgrade to doing nothing.
    /// </summary>
    [Fact]
    public async Task A_configured_but_unimplemented_transport_refuses_to_start()
    {
        RequireDatabase();

        await using var factory = new StudioApiFactory(
            fixture.ConnectionString, "Staging", passwordResetTransport: "Email");

        var failure = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var client = factory.CreateBrowserClient();
            await client.GetAsync("/health", Ct);
        });

        failure.Message.ShouldContain("no email transport is implemented");
        failure.Message.ShouldContain("D-8");
    }

    // -----------------------------------------------------------------------
    // W2-004 — the enabled TOTP secret is not recoverable
    // -----------------------------------------------------------------------

    /// <summary>
    /// The attack the first version allowed: a stolen session calls enroll, reads
    /// the live secret, computes a code, and disables MFA.
    /// </summary>
    [Fact]
    public async Task An_enabled_authenticator_secret_is_never_re_disclosed()
    {
        RequireDatabase();

        var email = UniqueEmail("mfa-secret");
        var client = NewClient();
        await client.SignUpAndSignInAsync(email, Password, Ct);

        var enroll = await client.PostAsync("/api/v1/auth/two-factor/enroll", new { }, Ct);
        var sharedKey = (await enroll.Content.ReadFromJsonAsync<JsonElement>(Ct))
            .GetProperty("sharedKey").GetString();

        (await client.PostAsync(
                "/api/v1/auth/two-factor/enable",
                new { code = Totp.Generate(sharedKey!) },
                Ct))
            .EnsureSuccessStatusCode();

        // Enrolling again must refuse rather than hand the secret back.
        var second = await client.PostAsync("/api/v1/auth/two-factor/enroll", new { }, Ct);

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var body = await second.Content.ReadAsStringAsync(Ct);
        body.ShouldNotContain(sharedKey!, Case.Insensitive);
        (await second.Content.ReadFromJsonAsync<JsonElement>(Ct))
            .GetProperty("code").GetString().ShouldBe("auth.two_factor_already_enabled");
    }

    /// <summary>Disabling needs the password too, not just a code.</summary>
    [Fact]
    public async Task Disabling_two_factor_requires_the_current_password()
    {
        RequireDatabase();

        var email = UniqueEmail("mfa-pwd");
        var client = NewClient();
        await client.SignUpAndSignInAsync(email, Password, Ct);

        var enroll = await client.PostAsync("/api/v1/auth/two-factor/enroll", new { }, Ct);
        var sharedKey = (await enroll.Content.ReadFromJsonAsync<JsonElement>(Ct))
            .GetProperty("sharedKey").GetString();

        (await client.PostAsync(
                "/api/v1/auth/two-factor/enable",
                new { code = Totp.Generate(sharedKey!) },
                Ct))
            .EnsureSuccessStatusCode();

        // A valid code with the wrong password is refused.
        (await client.PostAsync(
                "/api/v1/auth/two-factor/disable",
                new { currentPassword = "Wrong-Horse-9-Battery!", code = Totp.Generate(sharedKey!) },
                Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Both factors together succeed.
        (await client.PostAsync(
                "/api/v1/auth/two-factor/disable",
                new { currentPassword = Password, code = Totp.Generate(sharedKey!) },
                Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    /// <summary>
    /// Invalid factor-management codes count toward lockout, so the management
    /// endpoints cannot be used for unbounded online guessing.
    /// </summary>
    [Fact]
    public async Task Repeated_invalid_factor_codes_are_bounded_by_lockout()
    {
        RequireDatabase();

        var email = UniqueEmail("mfa-guess");
        var client = NewClient();
        await client.SignUpAndSignInAsync(email, Password, Ct);

        var enroll = await client.PostAsync("/api/v1/auth/two-factor/enroll", new { }, Ct);
        var sharedKey = (await enroll.Content.ReadFromJsonAsync<JsonElement>(Ct))
            .GetProperty("sharedKey").GetString();

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            (await client.PostAsync(
                    "/api/v1/auth/two-factor/enable", new { code = "000000" }, Ct))
                .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using var scope = Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByEmailAsync(email);

        (await users.IsLockedOutAsync(user!)).ShouldBeTrue(
            "invalid factor-management codes must count toward lockout");

        // The assertion the previous round was missing (W2-RR-002). Recording the
        // lockout proves nothing if the endpoint does not consult it: a *correct*
        // code after the threshold previously returned 204 and enabled MFA.
        var afterLockout = await client.PostAsync(
            "/api/v1/auth/two-factor/enable",
            new { code = Totp.Generate(sharedKey!) },
            Ct);

        afterLockout.StatusCode.ShouldBe(
            HttpStatusCode.Locked,
            "a correct code must be refused while the account is locked out");

        (await afterLockout.Content.ReadFromJsonAsync<JsonElement>(Ct))
            .GetProperty("code").GetString().ShouldBe("auth.locked_out");

        (await users.GetTwoFactorEnabledAsync(user!)).ShouldBeFalse(
            "MFA must not have been enabled by a request made while locked out");
    }

    /// <summary>
    /// Disabling is refused while locked out too — otherwise the throttle would
    /// block only the operation an attacker does not need.
    /// </summary>
    [Fact]
    public async Task Disabling_two_factor_is_refused_while_locked_out()
    {
        RequireDatabase();

        var email = UniqueEmail("mfa-locked-disable");
        var client = NewClient();
        await client.SignUpAndSignInAsync(email, Password, Ct);

        var enroll = await client.PostAsync("/api/v1/auth/two-factor/enroll", new { }, Ct);
        var sharedKey = (await enroll.Content.ReadFromJsonAsync<JsonElement>(Ct))
            .GetProperty("sharedKey").GetString();

        (await client.PostAsync(
                "/api/v1/auth/two-factor/enable",
                new { code = Totp.Generate(sharedKey!) },
                Ct))
            .EnsureSuccessStatusCode();

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await client.PostAsync(
                "/api/v1/auth/two-factor/disable",
                new { currentPassword = Password, code = "000000" },
                Ct);
        }

        var afterLockout = await client.PostAsync(
            "/api/v1/auth/two-factor/disable",
            new { currentPassword = Password, code = Totp.Generate(sharedKey!) },
            Ct);

        afterLockout.StatusCode.ShouldBe(HttpStatusCode.Locked);

        using var scope = Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByEmailAsync(email);

        (await users.GetTwoFactorEnabledAsync(user!)).ShouldBeTrue(
            "MFA must still be enabled: the disable request was made while locked out");
    }

    // -----------------------------------------------------------------------
    // W2-005 — the audit subject is the immutable user id
    // -----------------------------------------------------------------------

    /// <summary>
    /// The audit subject must be the user id, not the self-asserted email: with
    /// public registration and no confirmation, an address proves nothing.
    /// </summary>
    [Fact]
    public async Task The_audit_identity_is_the_immutable_user_id_not_the_email()
    {
        RequireDatabase();

        var email = UniqueEmail("audit");
        var client = NewClient();
        await client.SignUpAndSignInAsync(email, Password, Ct);

        var body = await client.Http.GetFromJsonAsync<JsonElement>("/api/v1/auth/me", Ct);

        var userId = body.GetProperty("userId").GetString();
        var auditIdentity = body.GetProperty("auditIdentity").GetString();

        auditIdentity.ShouldBe(userId);
        auditIdentity.ShouldNotBe(email, "a self-asserted address is not an audit subject");
        Guid.TryParse(auditIdentity, out _).ShouldBeTrue();

        // Email is still available, as display metadata.
        body.GetProperty("email").GetString().ShouldBe(email);
    }

    // -----------------------------------------------------------------------
    // W2-007 — malformed requests are client errors, not 500s
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("/api/v1/auth/register")]
    [InlineData("/api/v1/auth/login")]
    [InlineData("/api/v1/auth/forgot-password")]
    [InlineData("/api/v1/auth/reset-password")]
    public async Task An_empty_request_body_is_a_client_error_not_a_server_error(string path)
    {
        RequireDatabase();

        var response = await NewClient().PostAsync(path, new { }, Ct);

        ((int)response.StatusCode).ShouldBeLessThan(
            500, $"{path} must reject an empty body without a server error");
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // -----------------------------------------------------------------------
    // W2-009 — Swagger is not anonymous
    // -----------------------------------------------------------------------

    /// <summary>
    /// Deliberately uses a fresh anonymous client. The pre-existing Swagger test
    /// ran on a fixture that had already signed in, so it could not have detected
    /// anonymous access.
    /// </summary>
    [Theory]
    [InlineData("/swagger/v1/swagger.json")]
    [InlineData("/swagger/index.html")]
    public async Task Swagger_is_not_available_anonymously(string path)
    {
        RequireDatabase();

        var response = await NewClient().Http.GetAsync(path, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Swagger_is_available_to_an_authenticated_caller_in_development()
    {
        RequireDatabase();

        var client = NewClient();
        await client.SignUpAndSignInAsync(UniqueEmail("swagger"), Password, Ct);

        (await client.Http.GetAsync("/swagger/v1/swagger.json", Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // -----------------------------------------------------------------------
    // Security headers (S-25) and standing guards
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Security_headers_are_present_on_every_response()
    {
        RequireDatabase();

        var response = await NewClient().Http.GetAsync("/health", Ct);

        response.Headers.GetValues("X-Content-Type-Options").ShouldContain("nosniff");
        response.Headers.GetValues("X-Frame-Options").ShouldContain("DENY");
        response.Headers.GetValues("Referrer-Policy").ShouldContain("no-referrer");

        var csp = response.Headers.GetValues("Content-Security-Policy").ShouldHaveSingleItem();
        csp.ShouldContain("default-src 'none'");
        csp.ShouldContain("frame-ancestors 'none'");

        // Asserted because the test client speaks https; over plain http the
        // header is deliberately omitted so localhost is not pinned.
        response.Headers.GetValues("Strict-Transport-Security")
            .ShouldContain("max-age=31536000; includeSubDomains");
    }

    /// <summary>
    /// The guard that must survive every milestone. Authentication work has no
    /// business enabling provider spending, and this fails if it ever does.
    /// </summary>
    [Fact]
    public void The_billable_guard_is_disabled_in_the_test_host()
    {
        RequireDatabase();

        using var scope = Factory.Services.CreateScope();
        var options = scope.ServiceProvider
            .GetRequiredService<IOptions<Infrastructure.Providers.Stitch.StitchOptions>>();

        options.Value.BillableOperationsEnabled.ShouldBeFalse();
    }

    /// <summary>
    /// Guards the defect M01-02 uncovered: the test host must use the
    /// provisioned test database, not the developer's.
    ///
    /// The old override went through <c>ConfigureAppConfiguration</c>'s
    /// in-memory source, which did not win over the host's own
    /// <c>appsettings.json</c> — so every API-host test silently ran against
    /// whatever database the developer had configured. It went unnoticed because
    /// the readiness probe only needs *a* reachable database. Identity exposed it
    /// at once, since the auth tables existed only in the test database.
    /// </summary>
    [Fact]
    public async Task The_test_host_uses_the_provisioned_test_database()
    {
        RequireDatabase();

        using var scope = Factory.Services.CreateScope();
        var context = scope.ServiceProvider
            .GetRequiredService<Infrastructure.Persistence.AppDbContext>();

        var hostDatabase = context.Database.GetDbConnection().Database;
        var fixtureDatabase = new Npgsql.NpgsqlConnectionStringBuilder(fixture.ConnectionString).Database;

        hostDatabase.ShouldBe(fixtureDatabase);
        hostDatabase.ShouldStartWith("aids_test_", Case.Sensitive);

        // And the schema the host sees is the migrated one.
        var authTables = await context.Database
            .SqlQueryRaw<string>(
                "SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'auth'")
            .ToListAsync(Ct);

        authTables.ShouldContain("users");
        authTables.ShouldContain("roles");
        authTables.ShouldContain("data_protection_keys");
    }

    /// <summary>
    /// No role is seeded in M01-02. Decision D-3 selects Member and Approver,
    /// but enforcing them is M01-03, which must not start until M01-02 is
    /// accepted. This test fails if role seeding sneaks in early.
    /// </summary>
    [Fact]
    public async Task No_role_is_seeded_because_authorization_is_m01_03()
    {
        RequireDatabase();

        using var scope = Factory.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();

        (await roles.RoleExistsAsync("Member")).ShouldBeFalse();
        (await roles.RoleExistsAsync("Approver")).ShouldBeFalse();
    }
}
