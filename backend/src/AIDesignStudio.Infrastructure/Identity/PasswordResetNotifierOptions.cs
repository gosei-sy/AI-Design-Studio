namespace AIDesignStudio.Infrastructure.Identity;

/// <summary>
/// How password-reset tokens are delivered. Configured per environment.
///
/// <para>This type exists because of W2-003. The first implementation had one
/// notifier that threw at request time outside Development, which produced two
/// separate defects: production users had no recovery path at all, and the
/// throw-only-for-real-accounts behaviour was itself an account-existence oracle
/// (W2-002). Availability is now a startup concern — the host refuses to start
/// in a configuration it cannot honour — so no request can fail in a way that
/// depends on whose address was submitted.</para>
/// </summary>
public sealed class PasswordResetNotifierOptions
{
    public const string SectionName = "Auth:PasswordReset";

    /// <summary>The delivery mechanism. Defaults to none, which is fail-closed.</summary>
    public PasswordResetTransport Transport { get; set; } = PasswordResetTransport.None;

    /// <summary>
    /// Whether the development transport may write reset tokens to the log.
    ///
    /// Off by default, and honoured only in Development. A reset token is a
    /// credential; the review's point stands that shared CI or production log
    /// storage must never receive one, and a developer who wants it locally
    /// should have to ask.
    /// </summary>
    public bool LogTokensInDevelopment { get; set; }
}

public enum PasswordResetTransport
{
    /// <summary>
    /// No transport. Recovery is unavailable, and the host says so at startup
    /// rather than discovering it on the first real request.
    /// </summary>
    None = 0,

    /// <summary>
    /// Development only: the token goes to the application log, and only when
    /// <see cref="PasswordResetNotifierOptions.LogTokensInDevelopment"/> is set.
    /// </summary>
    DevelopmentLog = 1,

    /// <summary>
    /// A real transport. **Not implemented** — it needs an external mail service,
    /// which M01 is not authorized to add. Tracked as decision **D-8**; selecting
    /// it registers an implementation here. Configuring this value today is a
    /// startup error rather than a silent downgrade.
    /// </summary>
    Email = 2,
}
