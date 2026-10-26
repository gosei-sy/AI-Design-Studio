using AIDesignStudio.Api.Endpoints;
using AIDesignStudio.Infrastructure;
using AIDesignStudio.Infrastructure.Persistence;
using AIDesignStudio.SharedKernel;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

// Secrets come from the environment (and user-secrets locally), never from a
// committed file. See docs/04-engineering/SECURITY_REQUIREMENTS.md.
builder.Configuration.AddEnvironmentVariables(prefix: "AIDS_");

builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddHealthChecks()
    // Liveness must not depend on the database: a dead dependency should not
    // cause the orchestrator to restart an otherwise healthy process.
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
    .AddDbContextCheck<AppDbContext>("database", tags: ["ready"]);

builder.Services.AddCors(options => options.AddPolicy("studio", policy => policy
    .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

// Maps domain invariant violations onto RFC 9457 problem details. The domain's
// stable error code becomes the problem type, so clients branch on a contract
// rather than on message text.
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    ExceptionHandler = async context =>
    {
        var feature = context.Features.Get<IExceptionHandlerFeature>();

        if (feature?.Error is not DomainException domain)
        {
            return;
        }

        context.Response.StatusCode = StatusCodes.Status409Conflict;

        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Type = $"https://aidesignstudio.dev/problems/{domain.Code}",
            Title = "Domain invariant violated",
            Status = StatusCodes.Status409Conflict,
            Detail = domain.Message,
            Extensions = { ["code"] = domain.Code, ["traceId"] = context.TraceIdentifier },
        });
    },
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("studio");

app.MapHealthEndpoints();
app.MapProviderEndpoints();

app.Run();
