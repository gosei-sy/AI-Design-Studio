using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIDesignStudio.Infrastructure.Identity;

/// <summary>
/// Decides at startup whether this environment's authentication configuration is
/// one the application is willing to run.
///
/// <para>Both checks here exist because the review found the failure happening
/// somewhere worse. Password-reset availability used to be discovered on the
/// first real request, as a 500 for accounts that exist (W2-003). Data Protection
/// key encryption was never configured at all, so the key ring protecting every
/// session cookie was written to the database as plain text (W2-008).</para>
///
/// <para>**Production fails closed. Development warns loudly.** A developer
/// should not need a certificate to run the app locally, and an operator should
/// not be able to deploy a configuration that silently cannot recover a password
/// or silently stores its master key in the clear.</para>
/// </summary>
internal sealed class IdentityStartupValidation(
    IHostEnvironment environment,
    IOptions<PasswordResetNotifierOptions> passwordReset,
    IOptions<KeyManagementOptions> keyManagement,
    ILogger<IdentityStartupValidation> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ValidatePasswordResetTransport();
        ValidateKeyProtection();

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void ValidatePasswordResetTransport()
    {
        var transport = passwordReset.Value.Transport;

        if (transport == PasswordResetTransport.Email)
        {
            // Configured but unimplemented. Refusing to start is the only honest
            // response: starting would mean claiming a capability that silently
            // does nothing.
            throw new InvalidOperationException(
                $"{PasswordResetNotifierOptions.SectionName}:Transport is 'Email', but no email " +
                "transport is implemented. Adding one requires an authorized external mail " +
                "service — decision D-8. Set 'None' to run without recovery, or 'DevelopmentLog' " +
                "in Development.");
        }

        if (transport == PasswordResetTransport.DevelopmentLog && !environment.IsDevelopment())
        {
            // W2-RR-001. The first version rejected `Email` and rejected `None`
            // in Production, then returned for every other value — so
            // `DevelopmentLog` started happily in Production, where it either
            // writes reset credentials to production logs or silently delivers
            // nothing while startup reports success. A development transport is
            // a development transport.
            throw new InvalidOperationException(
                $"{PasswordResetNotifierOptions.SectionName}:Transport is 'DevelopmentLog', which " +
                $"is permitted only in Development. Current environment is " +
                $"'{environment.EnvironmentName}'. Configure a real transport (decision D-8) or " +
                "'None' to run explicitly without recovery.");
        }

        if (transport != PasswordResetTransport.None)
        {
            return;
        }

        if (environment.IsProduction())
        {
            throw new InvalidOperationException(
                "Password recovery has no delivery transport, so a user who forgets their " +
                "password would have no way back into their account. This is refused in " +
                $"Production. Configure {PasswordResetNotifierOptions.SectionName}:Transport " +
                "once decision D-8 provides a mail service.");
        }

        logger.LogWarning(
            "Password recovery is UNAVAILABLE in this environment: no delivery transport is " +
            "configured. /forgot-password will accept requests and send nothing. See decision D-8.");
    }

    private void ValidateKeyProtection()
    {
        // PersistKeysToDbContext turns OFF the automatic encryption-at-rest that
        // the default key location would have given us. Microsoft documents this;
        // the first implementation persisted keys and never configured a
        // protector, so the key ring sat in PostgreSQL as readable XML (W2-008).
        var hasProtector = keyManagement.Value.XmlEncryptor is not null;

        if (hasProtector)
        {
            return;
        }

        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "Data Protection keys would be persisted unencrypted. Choosing an explicit " +
                "persistence location disables automatic encryption at rest, so a protector must " +
                "be configured explicitly outside Development — a certificate, Key Vault or an " +
                "approved equivalent, to be selected with decision D-5. A read of the " +
                "auth.data_protection_keys table would otherwise expose the material protecting " +
                "every session cookie and reset token.");
        }

        logger.LogWarning(
            "Data Protection keys are persisted WITHOUT encryption at rest. Acceptable in " +
            "Development only; Production refuses to start in this state. See decision D-5.");
    }
}
