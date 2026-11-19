using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AIDesignStudio.Api.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        // Liveness: is the process up? Tagged checks only, so a database outage
        // does not get the process killed.
        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("live"),
            ResponseWriter = WriteResponse,
        }).WithTags("Health").AllowAnonymous();

        // Readiness: can the process serve traffic? Includes the database.
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready"),
            ResponseWriter = WriteResponse,
        }).WithTags("Health").AllowAnonymous();

        // These three are the only non-auth endpoints that stay anonymous.
        // Orchestrators probe them without credentials, and S-19 scopes the
        // authentication requirement to "all non-health endpoints" for exactly
        // that reason. Each opts out explicitly, because the host's fallback
        // policy requires an authenticated user by default.
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
            .WithTags("Health")
            .AllowAnonymous()
            .WithSummary("Minimal unauthenticated health probe.");
    }

    /// <summary>
    /// Emits check names, statuses and durations, but never a check's exception
    /// text: health endpoints are typically unauthenticated and exception text
    /// leaks connection strings and host names.
    /// </summary>
    private static async Task WriteResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                durationMs = e.Value.Duration.TotalMilliseconds,
                description = e.Value.Description,
            }),
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(payload));
    }
}
