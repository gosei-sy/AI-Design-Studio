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
        }).WithTags("Health");

        // Readiness: can the process serve traffic? Includes the database.
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready"),
            ResponseWriter = WriteResponse,
        }).WithTags("Health");

        app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
            .WithTags("Health")
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
