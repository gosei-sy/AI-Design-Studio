using AIDesignStudio.Api.Endpoints;
using AIDesignStudio.Api.Identity;
using AIDesignStudio.Infrastructure;
using AIDesignStudio.Infrastructure.Persistence;
using AIDesignStudio.SharedKernel;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

// Secrets come from the environment (and user-secrets locally), never from a
// committed file. See docs/04-engineering/SECURITY_REQUIREMENTS.md.
builder.Configuration.AddEnvironmentVariables(prefix: "AIDS_");

builder.Services.AddInfrastructure(builder.Configuration, OrganizationScoping.PerRequest);

// Authentication: D-7 / Option A — local Identity, same-origin HttpOnly
// cookie/BFF session. See ADR-0008. Adds the fallback "authenticated by
// default" policy, so §2 and §3 endpoints below must opt out explicitly.
builder.Services.AddSessionAuthentication();

// W2-007. Without this, [Required] on a request record is decoration: a `{}`
// body binds nulls, they reach the handler, and the caller gets a 500 from deep
// inside Identity. Registering validation rejects the request before the handler
// runs and returns a stable 400.
builder.Services.AddValidation();

// An unreadable body is the caller's error, so it is a 400 — in every
// environment. Minimal APIs default ThrowOnBadRequest to true in Development,
// expecting a developer exception page to render the BadHttpRequestException.
// This host has none (the exception handler below runs everywhere), so in
// Development a body that was not JSON became an unexplained 500 on every
// endpoint, while Production already answered 400. Found by M01-04's suite:
// ProjectEndpointsTests.A_malformed_body_is_a_400_problem_not_a_500.
builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(
    options => options.ThrowOnBadRequest = false);

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

// The session is a same-origin cookie, so the browser must be allowed to send
// it — which requires both AllowCredentials and an explicit origin list. A
// wildcard origin is invalid with credentials and would be rejected by the
// browser anyway; the allow-list is the point.
builder.Services.AddCors(options => options.AddPolicy("studio", policy => policy
    .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
    .AllowCredentials()
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

// W2-009. Swagger is middleware, so the endpoint fallback policy never applied
// to it: an anonymous caller got the full API contract while /api/v1/providers
// correctly returned 401. Development-only *and* behind authentication, so the
// acceptance criterion ("all non-health endpoints require authentication") and
// the implementation now agree.
if (app.Environment.IsDevelopment())
{
    app.UseWhen(
        context => context.Request.Path.StartsWithSegments("/swagger"),
        branch =>
        {
            branch.UseAuthentication();
            branch.Use(async (context, next) =>
            {
                if (context.User.Identity?.IsAuthenticated != true)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                await next();
            });
            branch.UseSwagger();
            branch.UseSwaggerUI();
        });
}

// Trust the scheme reported by a front-end proxy.
//
// The studio is served by a BFF that terminates TLS and forwards to this API —
// in development a Next.js rewrite, in deployment a reverse proxy. Without this,
// the forwarded hop arrives as plain HTTP, `Request.IsHttps` is false, and the
// Secure session and antiforgery cookies are refused with a 500 (M01-W2-RR-003).
//
// Only loopback proxies are trusted, which is the framework default and is what
// makes this safe: an arbitrary client cannot assert `X-Forwarded-Proto: https`
// to make the API believe an insecure request was secure. A deployed environment
// must add its own proxy's address.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
});

app.UseStudioSecurityHeaders();

app.UseCors("studio");

// Order matters: authentication populates the principal, authorization enforces
// the fallback policy, antiforgery reads its cookie. Antiforgery after
// authentication so a token is bound to the established session.
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapHealthEndpoints();
app.MapAuthEndpoints();
app.MapOrganizationEndpoints();
app.MapProjectEndpoints();
app.MapProviderEndpoints();

app.Run();
