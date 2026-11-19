namespace AIDesignStudio.Application.Identity;

/// <summary>
/// Delivers a password-reset token to the account owner.
///
/// A port rather than a concrete sender because M01-02 is authorized to add no
/// infrastructure and no external service, so there is no mail transport to call.
/// The interface fixes the shape now; a real sender replaces the registration in
/// M02 alongside the deployment decision (D-5) without anything above this layer
/// changing.
///
/// <para><b>The token must never be returned to the caller.</b> Putting it in an
/// HTTP response would mean anyone who can name an email address can reset that
/// account's password, which is worse than having no reset flow at all. The
/// endpoint therefore responds identically whether or not the address exists —
/// see <c>AuthEndpoints</c> — and the token travels only through this
/// port.</para>
/// </summary>
public interface IPasswordResetNotifier
{
    /// <param name="email">The address the reset was requested for.</param>
    /// <param name="resetToken">
    /// The single-use token produced by the identity store. Treat it as a
    /// credential: never log it at a level that reaches shared log storage, and
    /// never include it in a response body.
    /// </param>
    Task SendPasswordResetAsync(string email, string resetToken, CancellationToken cancellationToken = default);
}
