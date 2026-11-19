namespace AIDesignStudio.Api.Identity;

/// <summary>
/// Response security headers. Closes **S-25**.
///
/// Set in middleware rather than per-endpoint so a new endpoint cannot be added
/// without them — the same "closed by default" reasoning as the fallback
/// authorization policy.
/// </summary>
internal static class SecurityHeaders
{
    public static IApplicationBuilder UseStudioSecurityHeaders(this WebApplication app)
    {
        var isDevelopment = app.Environment.IsDevelopment();

        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;

            // Stops a browser from second-guessing a declared Content-Type,
            // which is how a JSON response gets treated as HTML and executed.
            headers["X-Content-Type-Options"] = "nosniff";

            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";

            // This host serves JSON, plus Swagger UI in development. Nothing
            // else should load, so the policy is correspondingly narrow:
            // default-src 'none' denies by default and each allowance is
            // deliberate. frame-ancestors 'none' is the modern equivalent of
            // X-Frame-Options, kept alongside it for older browsers.
            headers["Content-Security-Policy"] = isDevelopment
                ? "default-src 'none'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; " +
                  "script-src 'self' 'unsafe-inline'; connect-src 'self'; frame-ancestors 'none'; " +
                  "base-uri 'none'; form-action 'none'"
                : "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

            // HSTS is only meaningful over HTTPS, and asserting it over plain
            // HTTP in development would pin localhost to HTTPS in the
            // developer's browser — a genuinely annoying thing to debug.
            if (context.Request.IsHttps)
            {
                headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
            }

            await next();
        });

        return app;
    }
}
