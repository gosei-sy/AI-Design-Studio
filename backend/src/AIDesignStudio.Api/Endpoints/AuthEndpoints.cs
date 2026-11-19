using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using AIDesignStudio.Api.Identity;
using AIDesignStudio.Application.Identity;
using AIDesignStudio.Infrastructure.Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace AIDesignStudio.Api.Endpoints;

/// <summary>
/// Authentication surface for decision D-7 / Option A. See ADR-0008.
///
/// Three rules shape every handler here:
///
/// <list type="number">
/// <item><b>No account enumeration.</b> Registration, login and password reset
/// must not let an unauthenticated caller distinguish "no such address" from
/// "wrong password" or "address exists". Responses are deliberately identical.</item>
/// <item><b>No credential or token in a response body.</b> The reset token goes
/// through <see cref="IPasswordResetNotifier"/> only. The session is a cookie the
/// browser cannot read.</item>
/// <item><b>State-changing endpoints validate antiforgery.</b> A cookie travels
/// automatically, so without a token a third-party page could act as the user.</item>
/// </list>
/// </summary>
internal static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var environment = app.ServiceProvider.GetRequiredService<IHostEnvironment>();

        var group = app.MapGroup("/api/v1/auth")
            .WithTags("Authentication")

            // Applies to every endpoint in the group, so a malformed body is a
            // 400 before any handler or store is touched (W2-007).
            .AddEndpointFilter<RequestValidationEndpointFilter>();

        // --- Antiforgery token ------------------------------------------------
        // Anonymous and safe: a caller needs the token before it can log in.
        group.MapGet("/antiforgery-token", (IAntiforgery antiforgery, HttpContext http) =>
            {
                var tokens = antiforgery.GetAndStoreTokens(http);
                return Results.Ok(new { token = tokens.RequestToken });
            })
            .AllowAnonymous()
            .WithSummary("Issues an antiforgery token for state-changing requests.");

        // --- Registration -----------------------------------------------------
        //
        // **Development only.** Outside Development the route is not mapped at
        // all, so a request for it is a genuine 404 from the router: no handler,
        // no store touched, no attack surface.
        //
        // D-9 / ADR-0009: public self-registration never grants an organization
        // membership or a role, and outside Development it is disabled entirely.
        // Accounts come from trusted operator provisioning. A 403 would have been
        // the smaller change, but it leaves a live handler one configuration
        // mistake away from being reachable — and advertises that registration
        // exists somewhere.
        //
        // The operational consequence is real and is stated in ADR-0009: with no
        // registration and no reset delivery (D-8), a provisioned user's first
        // password is set by the operator and communicated out of band.
        if (environment.IsDevelopment())
        {
            MapRegistration(group);
        }

        MapSessionEndpoints(group);
        MapTwoFactorEndpoints(group);

        return app;
    }

    private static void MapRegistration(RouteGroupBuilder group) =>
        group.MapPost("/register", async (
                RegisterRequest request,
                UserManager<ApplicationUser> users) =>
            {
                // Password policy is evaluated FIRST, against a detached user, so
                // the verdict cannot depend on whether the address exists.
                //
                // W2-002. The first version looked the account up first and
                // returned 202 early for a duplicate — so a weak password with an
                // existing address gave 202 while the same weak password with a
                // new address gave 400. The difference was a reliable
                // account-existence oracle, and the original test missed it by
                // only ever probing duplicates with a *strong* password.
                var policy = await ValidatePasswordAsync(users, request.Password);

                if (!policy.Succeeded)
                {
                    return IdentityProblem(policy, "auth.registration_failed");
                }

                var existing = await users.FindByEmailAsync(request.Email);

                if (existing is not null)
                {
                    // Identical to the success response below.
                    return Accepted();
                }

                var user = new ApplicationUser(request.Email);
                var result = await users.CreateAsync(user, request.Password);

                if (!result.Succeeded)
                {
                    // Reachable for reasons unrelated to the password (a race on
                    // the unique index, for instance). Reported as the same code,
                    // so the shape does not vary with the cause.
                    return IdentityProblem(result, "auth.registration_failed");
                }

                return Accepted();
            })
            .AllowAnonymous()
            .AddEndpointFilter<AntiforgeryEndpointFilter>()
            .WithSummary("Creates an account. Responds identically whether or not the address was already taken.");

    /// <summary>
    /// Login, logout, identity and the password lifecycle. Mapped in every
    /// environment — unlike registration, these are how a provisioned account
    /// uses the system.
    /// </summary>
    private static void MapSessionEndpoints(RouteGroupBuilder group)
    {
        // --- Login ------------------------------------------------------------
        group.MapPost("/login", async (
                LoginRequest request,
                SignInManager<ApplicationUser> signIn,
                UserManager<ApplicationUser> users) =>
            {
                var user = await users.FindByEmailAsync(request.Email);

                if (user is null)
                {
                    // Hash a throwaway password so a missing account costs
                    // roughly the same time as a wrong password. Without this the
                    // response time itself reveals which addresses exist.
                    _ = users.PasswordHasher.HashPassword(new ApplicationUser(), request.Password);
                    return InvalidCredentials();
                }

                var attempt = await signIn.CheckPasswordSignInAsync(
                    user, request.Password, lockoutOnFailure: true);

                // Lockout is NOT reported distinctly to an anonymous caller.
                //
                // W2-002. A 423 told an attacker two things for free: that the
                // address exists, and that their guessing had taken effect. Both
                // responses are now the same 401, so the lockout is invisible
                // from outside.
                //
                // The cost is real and worth naming: a locked-out legitimate user
                // sees "invalid credentials" and no explanation of why a password
                // they know is correct stopped working. The honest fix is to tell
                // them by email, which needs the transport D-8 covers. Until then
                // silence is the safer of two bad options, because the
                // alternative hands the oracle to everyone.
                if (attempt.IsLockedOut || !attempt.Succeeded)
                {
                    return InvalidCredentials();
                }

                if (await users.GetTwoFactorEnabledAsync(user))
                {
                    if (string.IsNullOrWhiteSpace(request.TwoFactorCode))
                    {
                        // No session is issued. The caller must come back with a
                        // code; the password alone gets nothing.
                        return Results.Ok(new LoginResponse(
                            Authenticated: false, RequiresTwoFactor: true, Email: null));
                    }

                    var codeValid = await users.VerifyTwoFactorTokenAsync(
                        user,
                        users.Options.Tokens.AuthenticatorTokenProvider,
                        request.TwoFactorCode.Replace(" ", string.Empty, StringComparison.Ordinal));

                    if (!codeValid)
                    {
                        // Counts toward lockout: an attacker past the password
                        // must not get unlimited attempts at the second factor.
                        await users.AccessFailedAsync(user);
                        return InvalidCredentials();
                    }
                }

                await users.ResetAccessFailedCountAsync(user);
                await signIn.SignInAsync(user, isPersistent: false);

                return Results.Ok(new LoginResponse(
                    Authenticated: true, RequiresTwoFactor: false, Email: user.Email));
            })
            .AllowAnonymous()
            .AddEndpointFilter<AntiforgeryEndpointFilter>()
            .WithSummary("Signs in and issues the session cookie. Reports whether a second factor is required.");

        // --- Logout -----------------------------------------------------------
        group.MapPost("/logout", async (SignInManager<ApplicationUser> signIn) =>
            {
                await signIn.SignOutAsync();
                return Results.NoContent();
            })
            .AddEndpointFilter<AntiforgeryEndpointFilter>()
            .WithSummary("Clears the session cookie.");

        // --- Who am I ---------------------------------------------------------
        group.MapGet("/me", (ICurrentUser currentUser) => Results.Ok(new
        {
            userId = currentUser.UserId,
            email = currentUser.Email,
            auditIdentity = currentUser.RequireAuditIdentity(),
        }))
            .WithSummary("The authenticated caller, as the domain will record them.");

        // --- Change password --------------------------------------------------
        group.MapPost("/change-password", async (
                ChangePasswordRequest request,
                ClaimsPrincipal principal,
                UserManager<ApplicationUser> users,
                SignInManager<ApplicationUser> signIn) =>
            {
                var user = await users.GetUserAsync(principal);

                if (user is null)
                {
                    return Results.Unauthorized();
                }

                var result = await users.ChangePasswordAsync(
                    user, request.CurrentPassword, request.NewPassword);

                if (!result.Succeeded)
                {
                    return IdentityProblem(result, "auth.password_change_failed");
                }

                // Changing the password rotates the security stamp, which
                // invalidates other sessions. Re-issue this one so the caller is
                // not signed out of the request they are in the middle of.
                await signIn.RefreshSignInAsync(user);

                return Results.NoContent();
            })
            .AddEndpointFilter<AntiforgeryEndpointFilter>()
            .WithSummary("Changes the caller's password and re-issues their session.");

        // --- Forgot password --------------------------------------------------
        group.MapPost("/forgot-password", async (
                ForgotPasswordRequest request,
                UserManager<ApplicationUser> users,
                IPasswordResetNotifier notifier,
                CancellationToken cancellationToken) =>
            {
                // W2-002 / W2-003. The first version called the notifier only
                // for a real account, and the only notifier threw outside
                // Development — so a known address produced 500 and an unknown
                // one 202. The failure mode itself was the oracle.
                //
                // Transport availability is now decided at startup, not per
                // request (see PasswordResetNotifierOptions and the host's
                // startup check), so this handler cannot fail in a way that
                // depends on the submitted address.
                var user = await users.FindByEmailAsync(request.Email);

                if (user is not null)
                {
                    var token = await users.GeneratePasswordResetTokenAsync(user);
                    await notifier.SendPasswordResetAsync(request.Email, token, cancellationToken);
                }

                // 202 whether or not the address exists, and the token is never in
                // this response. Both are required: either alone would leak.
                return Accepted();
            })
            .AllowAnonymous()
            .AddEndpointFilter<AntiforgeryEndpointFilter>()
            .WithSummary("Starts a password reset. Responds identically for unknown addresses.");

        // --- Reset password ---------------------------------------------------
        group.MapPost("/reset-password", async (
                ResetPasswordRequest request,
                UserManager<ApplicationUser> users) =>
            {
                var user = await users.FindByEmailAsync(request.Email);

                if (user is null)
                {
                    // Same shape as an invalid token, for the same reason.
                    return ResetFailed();
                }

                var result = await users.ResetPasswordAsync(
                    user, request.Token, request.NewPassword);

                return result.Succeeded ? Results.NoContent() : ResetFailed();
            })
            .AllowAnonymous()
            .AddEndpointFilter<AntiforgeryEndpointFilter>()
            .WithSummary("Completes a password reset using the emailed token.");
    }

    /// <summary>
    /// TOTP enrollment. This is the MFA *policy* surface Option A owes: a user
    /// can enrol, confirm and withdraw a second factor.
    /// </summary>
    private static void MapTwoFactorEndpoints(IEndpointRouteBuilder group)
    {
        group.MapPost("/two-factor/enroll", async (
                ClaimsPrincipal principal,
                UserManager<ApplicationUser> users) =>
            {
                var user = await users.GetUserAsync(principal);

                if (user is null)
                {
                    return Results.Unauthorized();
                }

                // W2-004. The first version returned the existing key even when
                // the factor was already enabled — so a stolen session could read
                // the live secret, compute a valid code, and disable MFA. The
                // disable endpoint's "requires a current code" protection was
                // defeated by the enrol endpoint handing out the means to produce
                // one. A second factor whose secret is readable from a session is
                // not a second factor.
                if (await RefuseIfLockedOutAsync(users, user) is { } locked)
                {
                    return locked;
                }

                if (await users.GetTwoFactorEnabledAsync(user))
                {
                    return Results.Problem(
                        type: "https://aidesignstudio.dev/problems/auth.two_factor_already_enabled",
                        title: "Two-factor authentication is already enabled",
                        statusCode: StatusCodes.Status409Conflict,
                        detail: "The existing authenticator secret is never re-disclosed. To move " +
                                "to a new device, disable two-factor authentication first — which " +
                                "requires your password and a current code — then enrol again.",
                        extensions: new Dictionary<string, object?>
                        {
                            ["code"] = "auth.two_factor_already_enabled",
                        });
                }

                // Always a fresh secret. Re-issuing a pending one would let an
                // attacker who briefly held a session keep a secret that the real
                // owner later enables.
                await users.ResetAuthenticatorKeyAsync(user);
                var key = await users.GetAuthenticatorKeyAsync(user);

                // The shared secret has to reach the authenticator app somehow, so
                // unlike every other secret in this file it is returned — to the
                // authenticated owner, over an authenticated request, and only
                // while the factor is *not* yet enabled. Enabling still requires
                // proving possession.
                return Results.Ok(new { sharedKey = key, isEnabled = false });
            })
            .AddEndpointFilter<AntiforgeryEndpointFilter>()
            .WithSummary("Issues the caller's authenticator shared key. Does not enable the factor.");

        group.MapPost("/two-factor/enable", async (
                TwoFactorCodeRequest request,
                ClaimsPrincipal principal,
                UserManager<ApplicationUser> users) =>
            {
                var user = await users.GetUserAsync(principal);

                if (user is null)
                {
                    return Results.Unauthorized();
                }

                if (await RefuseIfLockedOutAsync(users, user) is { } locked)
                {
                    return locked;
                }

                if (!await VerifyFactorCodeAsync(users, user, request.Code))
                {
                    return InvalidTwoFactorCode();
                }

                await users.ResetAccessFailedCountAsync(user);
                await users.SetTwoFactorEnabledAsync(user, true);
                return Results.NoContent();
            })
            .AddEndpointFilter<AntiforgeryEndpointFilter>()
            .WithSummary("Enables two-factor authentication after verifying a code.");

        group.MapPost("/two-factor/disable", async (
                DisableTwoFactorRequest request,
                ClaimsPrincipal principal,
                UserManager<ApplicationUser> users) =>
            {
                var user = await users.GetUserAsync(principal);

                if (user is null)
                {
                    return Results.Unauthorized();
                }

                // Removing a factor requires re-proving *both* factors: the
                // current password and a current code.
                //
                // W2-004. A code alone was not enough, because the enrol endpoint
                // had been handing out the secret needed to generate one. The
                // password requirement means a stolen session is not sufficient
                // even if the secret leaks by some other route — stripping MFA
                // should be at least as hard as using it.
                if (await RefuseIfLockedOutAsync(users, user) is { } locked)
                {
                    return locked;
                }

                if (!await users.CheckPasswordAsync(user, request.CurrentPassword))
                {
                    await users.AccessFailedAsync(user);
                    return InvalidCredentials();
                }

                if (!await VerifyFactorCodeAsync(users, user, request.Code))
                {
                    return InvalidTwoFactorCode();
                }

                await users.ResetAccessFailedCountAsync(user);
                await users.SetTwoFactorEnabledAsync(user, false);
                await users.ResetAuthenticatorKeyAsync(user);

                return Results.NoContent();
            })
            .AddEndpointFilter<AntiforgeryEndpointFilter>()
            .WithSummary("Disables two-factor authentication after verifying a current code.");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static IResult Accepted() => Results.Accepted();

    /// <summary>
    /// Runs the configured password validators against a detached user, so the
    /// verdict is a function of the password alone and never of whether an
    /// account exists (W2-002).
    /// </summary>
    private static async Task<IdentityResult> ValidatePasswordAsync(
        UserManager<ApplicationUser> users, string password)
    {
        var failures = new List<IdentityError>();

        foreach (var validator in users.PasswordValidators)
        {
            var result = await validator.ValidateAsync(users, new ApplicationUser(), password);

            if (!result.Succeeded)
            {
                failures.AddRange(result.Errors);
            }
        }

        return failures.Count == 0 ? IdentityResult.Success : IdentityResult.Failed([.. failures]);
    }

    /// <summary>
    /// One response for every credential failure — unknown address, wrong
    /// password, wrong second factor. Distinguishing them would hand an attacker
    /// a free account-existence oracle.
    /// </summary>
    private static IResult InvalidCredentials() => Results.Problem(
        type: "https://aidesignstudio.dev/problems/auth.invalid_credentials",
        title: "Invalid credentials",
        statusCode: StatusCodes.Status401Unauthorized,
        detail: "The email address or password is incorrect.",
        extensions: new Dictionary<string, object?> { ["code"] = "auth.invalid_credentials" });

    /// <summary>
    /// Refuses factor management while the account is locked out.
    ///
    /// <para>W2-RR-002. The previous version called <c>AccessFailedAsync</c> on an
    /// invalid code and never checked <c>IsLockedOutAsync</c>, so the counter
    /// incremented and nothing consulted it: after five wrong codes the sixth
    /// *correct* code still enabled MFA and returned 204.
    /// <c>VerifyTwoFactorTokenAsync</c> verifies a token; it does not enforce
    /// lockout. **A flag is not a guard unless the protected operation reads
    /// it.**</para>
    ///
    /// <para><b>What happens to the session.</b> The session is left intact and
    /// only factor management is refused. Signing the caller out would let anyone
    /// holding a session lock themselves out of the application by guessing
    /// codes, which converts a throttle into a self-inflicted denial of service.
    /// The protection needed here is on the factor-management operation, and that
    /// is where it is applied. A 423 is returned rather than a generic error
    /// because the caller is already authenticated — the non-disclosure rule in
    /// W2-002 exists to protect *anonymous* probes, and withholding the reason
    /// from the account's own owner would be unhelpful for no security gain.</para>
    /// </summary>
    private static async Task<IResult?> RefuseIfLockedOutAsync(
        UserManager<ApplicationUser> users, ApplicationUser user)
    {
        if (!await users.IsLockedOutAsync(user))
        {
            return null;
        }

        return Results.Problem(
            type: "https://aidesignstudio.dev/problems/auth.locked_out",
            title: "Account locked",
            statusCode: StatusCodes.Status423Locked,
            detail: "Too many failed attempts. Two-factor settings cannot be changed until the " +
                    "lockout expires.",
            extensions: new Dictionary<string, object?> { ["code"] = "auth.locked_out" });
    }

    /// <summary>
    /// Verifies a factor-management code and **counts a failure toward lockout**.
    ///
    /// W2-004: the management endpoints previously allowed unbounded online
    /// guessing, because only the login-time branch called
    /// <c>AccessFailedAsync</c> — contradicting the ADR's claim that failed
    /// second factors count toward lockout.
    /// </summary>
    private static async Task<bool> VerifyFactorCodeAsync(
        UserManager<ApplicationUser> users, ApplicationUser user, string code)
    {
        var valid = await users.VerifyTwoFactorTokenAsync(
            user,
            users.Options.Tokens.AuthenticatorTokenProvider,
            code.Replace(" ", string.Empty, StringComparison.Ordinal));

        if (!valid)
        {
            await users.AccessFailedAsync(user);
        }

        return valid;
    }

    private static IResult InvalidTwoFactorCode() => Results.Problem(
        type: "https://aidesignstudio.dev/problems/auth.invalid_two_factor_code",
        title: "Invalid code",
        statusCode: StatusCodes.Status400BadRequest,
        detail: "The authenticator code did not verify.",
        extensions: new Dictionary<string, object?> { ["code"] = "auth.invalid_two_factor_code" });

    private static IResult ResetFailed() => Results.Problem(
        type: "https://aidesignstudio.dev/problems/auth.reset_failed",
        title: "Password reset failed",
        statusCode: StatusCodes.Status400BadRequest,
        detail: "The reset token is invalid or has expired. Request a new one.",
        extensions: new Dictionary<string, object?> { ["code"] = "auth.reset_failed" });

    private static IResult IdentityProblem(IdentityResult result, string code) => Results.Problem(
        type: $"https://aidesignstudio.dev/problems/{code}",
        title: "Identity operation failed",
        statusCode: StatusCodes.Status400BadRequest,
        detail: string.Join(" ", result.Errors.Select(e => e.Description)),
        extensions: new Dictionary<string, object?> { ["code"] = code });

    // A note on reset tokens, because the obvious-looking code here is wrong.
    //
    // The first version base64url-decoded the incoming token before handing it to
    // ResetPasswordAsync, copying a pattern from samples that encode the token
    // into a URL and decode it on the way back. GeneratePasswordResetTokenAsync
    // already returns a usable string, so decoding it *succeeded* and produced
    // garbage — every valid reset was rejected. The token is now passed through
    // untouched, and A_forgotten_password_can_be_reset_with_the_token_from_the_notifier
    // is what caught it.
}

// On [property: Required] rather than [Required].
//
// An attribute on a positional record parameter targets the *parameter*. The
// DataAnnotations Validator reads *property* attributes, so plain [Required]
// here was invisible to it — which is why an empty body still reached the
// handler and produced a 500 even after validation was wired up (W2-007). The
// `property:` target is what puts the attribute where the validator looks.
internal sealed record RegisterRequest([property: Required] string Email, [property: Required] string Password);

internal sealed record LoginRequest(
    [property: Required] string Email, [property: Required] string Password, string? TwoFactorCode = null);

internal sealed record LoginResponse(bool Authenticated, bool RequiresTwoFactor, string? Email);

internal sealed record ChangePasswordRequest(
    [property: Required] string CurrentPassword, [property: Required] string NewPassword);

internal sealed record ForgotPasswordRequest([property: Required] string Email);

internal sealed record ResetPasswordRequest(
    [property: Required] string Email, [property: Required] string Token, [property: Required] string NewPassword);

internal sealed record TwoFactorCodeRequest([property: Required] string Code);

/// <summary>Disabling a factor re-proves both: password and current code (W2-004).</summary>
internal sealed record DisableTwoFactorRequest(
    [property: Required] string CurrentPassword, [property: Required] string Code);
