using AIDesignStudio.Application.Identity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIDesignStudio.Infrastructure.Identity;

/// <summary>
/// The no-transport notifier. It accepts the call and delivers nothing.
///
/// <para><b>Why this does not throw.</b> The first version threw outside
/// Development, which looked fail-closed and was worse than useless: the throw
/// happened only when a *real* account submitted a reset, so `/forgot-password`
/// returned 500 for addresses that exist and 202 for addresses that do not — an
/// account-existence oracle on an anonymous endpoint (W2-002, W2-003).</para>
///
/// <para>Availability is now settled at startup. If recovery is unavailable the
/// host either refuses to start (Production) or logs one prominent warning at
/// boot (Development), and this notifier is a deliberate sink so the request path
/// behaves identically for every address. Fail-closed belongs at the point of
/// configuration, not at the point where a user asks for help.</para>
///
/// A real transport is decision **D-8**.
/// </summary>
internal sealed class LoggingPasswordResetNotifier(
    IHostEnvironment environment,
    IOptions<PasswordResetNotifierOptions> options,
    ILogger<LoggingPasswordResetNotifier> logger) : IPasswordResetNotifier
{
    public Task SendPasswordResetAsync(
        string email, string resetToken, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;

        // Defence in depth (W2-RR-001). Startup already refuses
        // DevelopmentLog outside Development, but this notifier previously
        // decided whether to log a credential purely from configuration values —
        // so a misconfiguration, or a future code path that skipped the startup
        // check, would have written reset tokens to a production log. The
        // environment is now part of the condition, not just the startup gate.
        if (settings.Transport == PasswordResetTransport.DevelopmentLog
            && settings.LogTokensInDevelopment
            && environment.IsDevelopment())
        {
            // Opt-in, Development-only, and never on by default: a reset token is
            // a credential, and shared log storage must not receive one.
            logger.LogWarning(
                "DEVELOPMENT ONLY — password reset token for {Email}: {ResetToken}",
                email, resetToken);

            return Task.CompletedTask;
        }

        // No transport. Logged without the token, at a level that will be noticed,
        // so an operator can see that recovery was requested and not delivered.
        logger.LogError(
            "A password reset was requested for {Email} but no delivery transport is " +
            "configured, so no token was sent. Recovery is unavailable in this " +
            "environment — see decision D-8.",
            email);

        return Task.CompletedTask;
    }
}
