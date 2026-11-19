using Microsoft.AspNetCore.Antiforgery;

namespace AIDesignStudio.Api.Identity;

/// <summary>
/// Validates the antiforgery token on state-changing requests.
///
/// <para><b>Why this is not automatic.</b> ASP.NET Core's antiforgery middleware
/// validates minimal-API endpoints that bind a form. A JSON endpoint binds from
/// the body, so the middleware does not validate it — meaning a cookie-session
/// JSON API has <i>no</i> CSRF protection from middleware alone. That is the gap
/// this filter closes, and it is the obligation Option A carries that a bearer
/// token would not.</para>
///
/// <para>Safe methods are skipped. GET, HEAD, OPTIONS and TRACE must not change
/// state, so requiring a token on them would add ceremony without adding
/// safety — and would make the token-issuing endpoint itself unreachable.</para>
/// </summary>
internal sealed class AntiforgeryEndpointFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    private static readonly string[] SafeMethods = ["GET", "HEAD", "OPTIONS", "TRACE"];

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;

        if (SafeMethods.Contains(http.Request.Method, StringComparer.OrdinalIgnoreCase))
        {
            return await next(context);
        }

        try
        {
            await antiforgery.ValidateRequestAsync(http);
        }
        catch (AntiforgeryValidationException)
        {
            // 400, not 401: the caller may well be authenticated. What is missing
            // is proof that *this* request was intended, so telling them to log in
            // again would be misleading and would not help.
            return Results.Problem(
                type: "https://aidesignstudio.dev/problems/auth.antiforgery_failed",
                title: "Antiforgery validation failed",
                statusCode: StatusCodes.Status400BadRequest,
                detail: $"A valid antiforgery token is required. Fetch one from " +
                        $"GET /api/v1/auth/antiforgery-token and send it in the " +
                        $"{SessionAuthentication.AntiforgeryHeaderName} header.",
                extensions: new Dictionary<string, object?> { ["code"] = "auth.antiforgery_failed" });
        }

        return await next(context);
    }
}
