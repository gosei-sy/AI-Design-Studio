using AIDesignStudio.Application.Artifacts;
using AIDesignStudio.Application.Jobs;
using AIDesignStudio.Application.Providers;
using AIDesignStudio.Application.Storage;
using AIDesignStudio.Infrastructure.Artifacts;
using AIDesignStudio.Infrastructure.Jobs;
using AIDesignStudio.Infrastructure.Persistence;
using AIDesignStudio.Infrastructure.Providers;
using AIDesignStudio.Infrastructure.Providers.Stitch;
using AIDesignStudio.Infrastructure.Storage;
using AIDesignStudio.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AIDesignStudio.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Composition root for infrastructure. Both the API and the worker call this,
    /// so they share one wiring definition and cannot drift apart.
    /// </summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
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
