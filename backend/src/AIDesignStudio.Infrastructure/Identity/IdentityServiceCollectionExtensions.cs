using AIDesignStudio.Application.Identity;
using AIDesignStudio.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AIDesignStudio.Infrastructure.Identity;

/// <summary>
/// The persistence half of decision **D-7 / Option A**: ASP.NET Core Identity
/// backed by the existing PostgreSQL database. See ADR-0008.
///
/// <para>Deliberately stops short of the session. Cookie authentication,
/// <c>SignInManager</c> and <c>HttpContext</c> reading all live in the API host,
/// because they need the ASP.NET Core framework reference and because a worker
/// process has no session to manage. Infrastructure stays a class library whose
/// only web dependency is Identity's own store contract — which also keeps the
/// layering claim in ADR-0001 honest.</para>
///
/// Nothing here grants anything, and that remains true after M01-03.
/// Authorization — organization membership and the Member/Approver roles — is a
/// separate aggregate reached through
/// <c>Application.Organizations.IOrganizationContext</c>, and it is created only
/// by operator provisioning (ADR-0009). In particular the Identity role store
/// registered below is **not** how Member and Approver are represented: Identity
/// roles are global, and these are scoped to one organization.
/// </summary>
public static class IdentityServiceCollectionExtensions
{
    public static IServiceCollection AddStudioIdentityStores(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PasswordResetNotifierOptions>(
            configuration.GetSection(PasswordResetNotifierOptions.SectionName));

        // Keys in the database, not on the container filesystem. Cookie sessions
        // would otherwise be invalidated by every restart and unreadable by a
        // second instance.
        //
        // W2-008: choosing an explicit persistence location **disables** the
        // automatic encryption at rest that the default location provides, so a
        // protector has to be configured deliberately. None is registered here
        // because the right one depends on the deployment target (D-5), and
        // guessing would be worse than refusing — so IdentityStartupValidation
        // refuses to start outside Development until one exists.
        services.AddDataProtection()
            .PersistKeysToDbContext<AppDbContext>()
            .SetApplicationName("AIDesignStudio");

        services.AddHostedService<IdentityStartupValidation>();

        services.AddIdentityCore<ApplicationUser>(options =>
            {
                // --- Password lifecycle ---------------------------------------
                options.Password.RequiredLength = 12;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = true;

                // --- Lockout --------------------------------------------------
                // Five attempts, then fifteen minutes. Enabled for new users too,
                // which is not the framework default and is the case that
                // matters: a fresh account is the least-watched one.
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.Lockout.AllowedForNewUsers = true;

                // --- Account rules --------------------------------------------
                options.User.RequireUniqueEmail = true;

                // Email confirmation is NOT required to sign in, because M01-02
                // is authorized to add no mail transport — so no account could
                // ever be confirmed and nobody could log in. Stated here rather
                // than silently left at its default: enabling it belongs with the
                // real sender in M02.
                options.SignIn.RequireConfirmedEmail = false;

                options.Tokens.AuthenticatorTokenProvider = TokenOptions.DefaultAuthenticatorProvider;
            })
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<AppDbContext>();

        // Token providers (password reset, TOTP) and security-stamp revalidation
        // are added by the API host: both live in the ASP.NET Core shared
        // framework, and both are sign-in concerns rather than storage ones.

        services.AddScoped<IPasswordResetNotifier, LoggingPasswordResetNotifier>();

        return services;
    }
}
