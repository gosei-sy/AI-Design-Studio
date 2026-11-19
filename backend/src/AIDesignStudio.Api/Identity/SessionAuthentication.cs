using AIDesignStudio.Application.Identity;
using AIDesignStudio.Application.Organizations;
using AIDesignStudio.Infrastructure.Identity;
using AIDesignStudio.Infrastructure.Organizations;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;

namespace AIDesignStudio.Api.Identity;

/// <summary>
/// The session half of decision **D-7 / Option A**: a same-origin HttpOnly
/// cookie issued by this host, i.e. a BFF boundary. See ADR-0008.
///
/// The browser never receives a token of any kind — only an opaque, HttpOnly,
/// <c>SameSite=Strict</c> cookie it cannot read. That is the whole point of
/// choosing a cookie/BFF session over bearer tokens: an XSS cannot exfiltrate a
/// session it cannot see.
/// </summary>
internal static class SessionAuthentication
{
    /// <summary>
    /// The session cookie's name. The <c>__Host-</c> prefix is enforced by
    /// browsers: a cookie with it is rejected unless it is Secure, has path
    /// <c>/</c>, and carries no Domain attribute — so it cannot be scoped to a
    /// parent domain or planted by a sibling subdomain.
    /// </summary>
    public const string SessionCookieName = "__Host-aids.session";

    public static IServiceCollection AddSessionAuthentication(
        this IServiceCollection services,
        TimeSpan? securityStampValidationInterval = null)
    {
        // Token providers for password reset and TOTP, and SignInManager, which
        // Infrastructure deliberately leaves to the host.
        services.AddIdentityCore<ApplicationUser>()
            .AddDefaultTokenProviders()
            .AddSignInManager();

        // W2-001. Configuring the interval alone does nothing: the validator is
        // only invoked if the cookie's OnValidatePrincipal event calls it, which
        // the first version never wired up. So the interval was set, the ADR
        // claimed sessions were revoked on password change, and no session was
        // ever actually revoked.
        //
        // The interval is configurable because a test has to be able to observe
        // revocation without waiting fifteen minutes. Production default stands.
        services.Configure<SecurityStampValidatorOptions>(options =>
            options.ValidationInterval = securityStampValidationInterval
                ?? TimeSpan.FromMinutes(15));

        // AddIdentityCookies(), not a bare AddCookie.
        //
        // Identity uses four cookie schemes — Application, External,
        // TwoFactorRememberMe and TwoFactorUserId. Registering only the
        // application one compiled and signed users in happily, then threw
        // InvalidOperationException the first time the security-stamp validator
        // rejected a principal, because rejecting it calls SignOutAsync across
        // all four. The helper registers the set, which is why the review
        // recommended it.
        services
            .AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddIdentityCookies();

        services.ConfigureApplicationCookie(options =>
            {
                options.Cookie.Name = SessionCookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;

                // Strict, not Lax. A same-origin BFF never needs its session
                // cookie on a cross-site navigation, and Strict is the single
                // most effective CSRF mitigation available. The antiforgery
                // token is the second layer, not the only one.
                options.Cookie.SameSite = SameSiteMode.Strict;

                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;

                // An API must not answer an unauthenticated call with a 302 to a
                // login page: fetch() follows redirects, so the caller would see
                // a puzzling 200 carrying HTML instead of a 401 it can branch on.
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };

                // The event that makes revocation real. On each validation the
                // stamp in the cookie is compared with the one in the database;
                // a mismatch signs the principal out. Changing a password,
                // resetting it, or toggling MFA all rotate the stamp, so every
                // other session for that account stops working.
                //
                // Without this line the account-security guarantees in ADR-0008
                // are false (W2-001). Verified by
                // AuthenticationTests.Changing_the_password_revokes_other_sessions.
                options.Events.OnValidatePrincipal =
                    SecurityStampValidator.ValidatePrincipalAsync;
            });

        // Every endpoint requires an authenticated caller unless it opts out with
        // AllowAnonymous. A fallback policy is used rather than per-endpoint
        // attributes because the default must be "closed": a new endpoint added
        // next milestone is protected because nobody did anything, which is the
        // opposite of the usual failure where protection is forgotten. S-19.
        services.AddAuthorization(options =>
        {
            options.FallbackPolicy =
                new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .Build();

            // Organization-scoped roles (D-3, ADR-0009).
            options.AddOrganizationPolicies();
        });

        services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler,
            OrganizationRoleHandler>();

        // Turns a role policy's refusal into a coded 403 (M01-05). Scoped: it
        // reads the request's already-resolved membership.
        services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler,
            OrganizationForbiddenResultHandler>();

        // Resolves the caller's membership and opens the request's data scope.
        // Registered here rather than in AddInfrastructure because it depends on
        // ICurrentUser: there is no "current caller" in the worker or the
        // provisioning tool, and registering it for them broke their startup
        // outright. The scope itself comes from
        // AddInfrastructure(OrganizationScoping.PerRequest), which the API host
        // has to choose explicitly — and never System, asserted by
        // AuthorizationTests.The_api_host_is_never_system_scoped.
        services.AddScoped<IOrganizationContext, OrganizationContext>();

        // Antiforgery for the cookie session. Required by Option A: a cookie is
        // attached by the browser automatically, so without a token a third-party
        // page could trigger a state-changing request as the signed-in user.
        services.AddAntiforgery(options =>
        {
            options.HeaderName = AntiforgeryHeaderName;
            options.Cookie.Name = "__Host-aids.antiforgery";
            options.Cookie.HttpOnly = false;   // the SPA must read it to echo it back
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpContextCurrentUser>();

        return services;
    }

    /// <summary>The request header the SPA echoes the antiforgery token in.</summary>
    public const string AntiforgeryHeaderName = "X-CSRF-TOKEN";
}
