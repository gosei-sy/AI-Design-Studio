using AIDesignStudio.Application.Artifacts;
using AIDesignStudio.Application.Jobs;
using AIDesignStudio.Application.Organizations;
using AIDesignStudio.Application.Providers;
using AIDesignStudio.Application.Storage;
using AIDesignStudio.Infrastructure.Artifacts;
using AIDesignStudio.Infrastructure.Identity;
using AIDesignStudio.Infrastructure.Jobs;
using AIDesignStudio.Infrastructure.Organizations;
using AIDesignStudio.Infrastructure.Persistence;
using AIDesignStudio.Infrastructure.Providers;
using AIDesignStudio.Infrastructure.Providers.Stitch;
using AIDesignStudio.Infrastructure.Storage;
using AIDesignStudio.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AIDesignStudio.Infrastructure;

/// <summary>
/// How a host sees organization-scoped data. **A required choice**, not a
/// default (ADR-0009).
///
/// <para>It is a parameter rather than something a host remembers to register
/// because both ways of getting it wrong are bad and neither is loud. Forgetting
/// it entirely leaves every organization-scoped query returning nothing — a
/// worker that silently processes no jobs, which took a timed-out test to notice.
/// Defaulting it to <see cref="System"/> instead would make a forgotten
/// registration a cross-tenant read. A required parameter has no third
/// outcome.</para>
/// </summary>
public enum OrganizationScoping
{
    /// <summary>
    /// Resolved per request from the caller's membership, starting closed. For
    /// hosts that serve requests.
    /// </summary>
    PerRequest,

    /// <summary>
    /// Unscoped, deliberately. For processes with no caller to scope to: the job
    /// worker, which dispatches work for every tenant, and the provisioning tool,
    /// which must find an organization before any membership exists.
    /// </summary>
    System,
}

public static class DependencyInjection
{
    /// <summary>
    /// Composition root for infrastructure. Every host calls this, so they share
    /// one wiring definition and cannot drift apart.
    /// </summary>
    /// <param name="scoping">
    /// How this host sees organization-scoped data. See
    /// <see cref="OrganizationScoping"/> for why this is not optional.
    /// </param>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        OrganizationScoping scoping)
    {
        services.AddSingleton<IClock, SystemClock>();

        // --- Persistence ------------------------------------------------------
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Default is not configured. See .env.example.");

        services.AddDbContext<AppDbContext>(options => options
            .UseNpgsql(connectionString, npgsql => npgsql
                .MigrationsHistoryTable("__ef_migrations_history", "app"))
            .UseSnakeCaseNamingConvention());

        // --- Identity stores (D-7 / Option A; ADR-0008) -----------------------
        // Stores and password/lockout policy only. The cookie session,
        // SignInManager and HttpContext reading live in the API host: they need
        // the ASP.NET Core framework reference, and a worker has no session.
        // Registered in the shared root so both hosts agree about the EF model —
        // configuring Identity in one host only would yield two different
        // migration snapshots.
        services.AddStudioIdentityStores(configuration);

        // --- Organization scoping (D-9 / ADR-0009) ----------------------------
        if (scoping == OrganizationScoping.System)
        {
            services.AddSingleton<IOrganizationScope>(SystemOrganizationScope.Instance);
        }
        else
        {
            // Starts closed. Opened once per request by IOrganizationContext,
            // from a membership row and from nothing the caller sends.
            services.AddScoped<OrganizationScope>();
            services.AddScoped<IOrganizationScope>(sp => sp.GetRequiredService<OrganizationScope>());
        }

        // --- Organization provisioning (D-9 / ADR-0009) -----------------------
        // The provisioning service is shared: the console tool runs it, and the
        // integration tests drive it through the API host's container so they
        // exercise the real thing.
        //
        // Two things are deliberately NOT registered here.
        //
        // **IOrganizationContext** is deliberately not registered here, and that
        // is not a style preference — it resolves *the current caller's*
        // membership and needs ICurrentUser, which exists only where there is an
        // HTTP request. Registering it here made the worker and the console tool
        // fail at startup with an unresolvable dependency, which is how this
        // comment came to be written.
        services.AddScoped<OperatorProvisioningService>();

        // --- Asset storage ----------------------------------------------------
        // Local filesystem for development; the Azure Blob adapter replaces this
        // registration per environment without any change above this layer.
        services.Configure<LocalAssetStorageOptions>(
            configuration.GetSection(LocalAssetStorageOptions.SectionName));
        services.AddSingleton<IAssetStorage, LocalFileSystemAssetStorage>();

        // --- Durable jobs -----------------------------------------------------
        services.AddScoped<IJobQueue, PostgresJobQueue>();

        // --- Artifact parsing -------------------------------------------------
        services.AddSingleton<IDesignArtifactParser, HtmlDesignArtifactParser>();
        services.AddSingleton<IDesignArtifactParser, ScreenshotDesignArtifactParser>();

        // --- AI providers -----------------------------------------------------
        services.Configure<StitchOptions>(configuration.GetSection(StitchOptions.SectionName));
        services.AddSingleton<IStitchCommandRunner, StitchProcessCommandRunner>();
        services.AddSingleton<IDesignProvider, StitchDesignProvider>();
        services.AddSingleton<IDesignProviderRegistry, DesignProviderRegistry>();

        return services;
    }
}
