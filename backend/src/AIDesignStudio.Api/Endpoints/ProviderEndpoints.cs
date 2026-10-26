using AIDesignStudio.Application.Providers;

namespace AIDesignStudio.Api.Endpoints;

/// <summary>
/// Read-only diagnostics over the provider adapters.
///
/// Exists in M00 because "which provider capabilities are actually available in
/// this environment?" is the question the rest of the milestones depend on, and
/// it must be answerable without reading code.
/// </summary>
public static class ProviderEndpoints
{
    public static void MapProviderEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/providers").WithTags("Providers");

        group.MapGet("/", (IDesignProviderRegistry registry) => Results.Ok(
                registry.All.Select(p => new
                {
                    providerId = p.ProviderId,
                    capabilities = p.Capabilities.Select(c => c.Name).OrderBy(n => n),
                })))
            .WithSummary("Lists registered provider adapters and the capabilities each declares.");

        group.MapGet("/{providerId}/readiness", async (
                string providerId,
                IDesignProviderRegistry registry,
                CancellationToken cancellationToken) =>
            {
                var provider = registry.Find(providerId);

                if (provider is null)
                {
                    return Results.NotFound(new { error = $"No provider '{providerId}' is registered." });
                }

                var readiness = await provider.CheckReadinessAsync(cancellationToken);

                // Credential *names* only. Never echo a credential value.
                return Results.Ok(new
                {
                    readiness.ProviderId,
                    readiness.IsInstalled,
                    readiness.IsAuthenticated,
                    readiness.IsUsable,
                    readiness.Detail,
                    readiness.MissingCredentialNames,
                });
            })
            .WithSummary("Probes whether a provider is installed and authenticated. Never billable.");
    }
}
