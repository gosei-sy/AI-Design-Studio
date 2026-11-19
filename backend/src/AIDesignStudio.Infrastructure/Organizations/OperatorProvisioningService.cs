using AIDesignStudio.Domain.Organizations;
using AIDesignStudio.Infrastructure.Identity;
using AIDesignStudio.Infrastructure.Persistence;
using AIDesignStudio.SharedKernel;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AIDesignStudio.Infrastructure.Organizations;

/// <summary>What an operator asked for. No secret is part of this record.</summary>
/// <param name="Email">The account to provision. Created if it does not exist.</param>
/// <param name="OrganizationName">Selected if it exists, created if it does not.</param>
/// <param name="Roles">The exact role set the membership should end up with.</param>
/// <param name="Actor">
/// Who is doing this, as the operator states it. Recorded on the membership and
/// required: ADR-0009 defines proofing as the operator's recorded assertion that
/// the person was verified out of band, and an assertion nobody signed is not one.
/// </param>
public sealed record ProvisionRequest(
    string Email, string OrganizationName, OrganizationRoles Roles, string Actor);

/// <summary>
/// What happened. Safe to print in full — it contains no credential, no reset
/// token and no password.
/// </summary>
public sealed record ProvisionResult(
    Guid UserId,
    Guid OrganizationId,
    string OrganizationName,
    OrganizationRoles Roles,
    bool UserCreated,
    bool OrganizationCreated,
    string ProvisionedBy);

/// <summary>
/// Thrown when an operator asks for something the model refuses. Separate from
/// <see cref="DomainException"/> because these are tool-usage errors the operator
/// can fix, not domain invariant violations.
/// </summary>
public sealed class ProvisioningException(string code, string message)
    : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// The **only** way an account gains an organization membership or a role
/// (ADR-0009, D-9).
///
/// <para>Reachable from the <c>aids-provision</c> console tool and from tests.
/// There is no HTTP endpoint that calls it, no bootstrap route, and no
/// self-service path — asserted by
/// <c>AuthorizationTests.No_http_endpoint_can_grant_a_membership</c>. That is the
/// whole of the proofing control: access requires somebody with database
/// credentials and shell access to assert, under their own recorded name, that
/// they verified the person out of band.</para>
///
/// <para><b>Secrets.</b> A password reaches this service only through
/// <paramref name="providePassword"/>, invoked lazily and only when a new account
/// must be created. Nothing here logs it, returns it, or puts it in an exception
/// message. The tool reads it from standard input, never from
/// <c>args</c>: <c>/proc/&lt;pid&gt;/cmdline</c> is world-readable, so a password
/// passed as an argument is visible to every local user for the life of the
/// process — and lands in the operator's shell history besides.</para>
/// </summary>
public sealed class OperatorProvisioningService(
    UserManager<ApplicationUser> users,
    AppDbContext db,
    IClock clock,
    ILogger<OperatorProvisioningService> logger)
{
    public async Task<ProvisionResult> ProvisionAsync(
        ProvisionRequest request,
        Func<CancellationToken, Task<string>> providePassword,
        CancellationToken cancellationToken)
    {
        var email = (request.Email ?? string.Empty).Trim();
        var organizationName = (request.OrganizationName ?? string.Empty).Trim();
        var actor = (request.Actor ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(email))
        {
            throw new ProvisioningException("provisioning.email_required", "An email address is required.");
        }

        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new ProvisioningException(
                "provisioning.actor_required",
                "--actor is required: the membership records who asserted this person was verified.");
        }

        // Validated before anything is written, so a bad role set cannot leave a
        // half-provisioned account behind.
        if (request.Roles == OrganizationRoles.None
            || (request.Roles & ~OrganizationMembership.KnownRoles) != OrganizationRoles.None)
        {
            throw new ProvisioningException(
                "provisioning.invalid_roles",
                "At least one role is required, and only Member and Approver exist. " +
                "Use --role Member, --role Approver, or both.");
        }

        var provisionedBy = BuildProvisionedBy(actor);

        // One transaction. A user without a membership is a dead account and a
        // membership without its organization is impossible; neither should be
        // reachable because the process was interrupted halfway.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var (user, userCreated) = await FindOrCreateUserAsync(email, providePassword, cancellationToken);
        var (organization, organizationCreated) =
            await FindOrCreateOrganizationAsync(organizationName, cancellationToken);

        await ApplyMembershipAsync(user.Id, organization, request.Roles, provisionedBy, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // Logged without the password, and without the reset token that is never
        // generated here. Email is included because an operator needs to see
        // which account they just granted access to.
        logger.LogInformation(
            "Provisioned {Email} (user {UserId}) into organization {OrganizationName} " +
            "({OrganizationId}) with roles {Roles}. Asserted by {ProvisionedBy}.",
            email, user.Id, organization.Name, organization.Id, request.Roles, provisionedBy);

        return new ProvisionResult(
            user.Id, organization.Id, organization.Name, request.Roles,
            userCreated, organizationCreated, provisionedBy);
    }

    /// <summary>
    /// Withdraws access. Needed as much as granting it: without a withdrawal path
    /// the only way to remove a membership is a hand-written UPDATE, which is how
    /// the revocation audit fields end up blank.
    /// </summary>
    public async Task<Guid> RevokeAsync(string email, string actor, CancellationToken cancellationToken)
    {
        var user = await users.FindByEmailAsync((email ?? string.Empty).Trim())
            ?? throw new ProvisioningException(
                "provisioning.user_not_found", $"No account exists for '{email}'.");

        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new ProvisioningException(
                "provisioning.actor_required", "--actor is required: a revocation records who performed it.");
        }

        var membership = await db.OrganizationMemberships.FirstOrDefaultAsync(
            m => m.UserId == user.Id && m.Status == MembershipStatus.Active, cancellationToken)
            ?? throw new ProvisioningException(
                "provisioning.no_active_membership",
                $"'{email}' has no active membership to revoke.");

        membership.Revoke(BuildProvisionedBy(actor.Trim()), clock);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Revoked membership {MembershipId} for user {UserId}. Performed by {Actor}.",
            membership.Id, user.Id, actor);

        return membership.Id;
    }

    private async Task<(ApplicationUser User, bool Created)> FindOrCreateUserAsync(
        string email,
        Func<CancellationToken, Task<string>> providePassword,
        CancellationToken cancellationToken)
    {
        if (await users.FindByEmailAsync(email) is { } existing)
        {
            // An existing account's password is never touched. Re-provisioning
            // adjusts membership and roles; resetting a credential as a
            // side-effect of a role change would be a surprise with security
            // consequences.
            return (existing, false);
        }

        var password = await providePassword(cancellationToken);

        if (string.IsNullOrEmpty(password))
        {
            throw new ProvisioningException(
                "provisioning.password_required",
                "A password is required to create a new account. It is read from standard input, " +
                "never from the command line.");
        }

        var user = new ApplicationUser(email);
        var result = await users.CreateAsync(user, password);

        if (!result.Succeeded)
        {
            // Descriptions only — Identity never puts the password in them, and
            // the operator needs to know which policy rule failed.
            throw new ProvisioningException(
                "provisioning.user_creation_failed",
                string.Join(" ", result.Errors.Select(e => e.Description)));
        }

        return (user, true);
    }

    private async Task<(Organization Organization, bool Created)> FindOrCreateOrganizationAsync(
        string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ProvisioningException(
                "provisioning.organization_required", "--organization is required.");
        }

        var existing = await db.Organizations.FirstOrDefaultAsync(o => o.Name == name, cancellationToken);

        if (existing is not null)
        {
            return (existing, false);
        }

        var organization = Organization.Create(name, clock);
        db.Organizations.Add(organization);

        // Flushed before the membership is built so the foreign key has a row to
        // point at inside this transaction.
        await db.SaveChangesAsync(cancellationToken);

        return (organization, true);
    }

    private async Task ApplyMembershipAsync(
        Guid userId,
        Organization organization,
        OrganizationRoles roles,
        string provisionedBy,
        CancellationToken cancellationToken)
    {
        var active = await db.OrganizationMemberships.FirstOrDefaultAsync(
            m => m.UserId == userId && m.Status == MembershipStatus.Active, cancellationToken);

        if (active is null)
        {
            db.OrganizationMemberships.Add(OrganizationMembership.Provision(
                organization.Id, userId, roles, provisionedBy, clock));
            return;
        }

        if (active.OrganizationId != organization.Id)
        {
            // The MVP allows one active membership per user (ADR-0009). Silently
            // moving the account would revoke its access to the first
            // organization's data as a side effect of a command that reads like
            // it only adds something.
            throw new ProvisioningException(
                "provisioning.user_already_provisioned",
                "This account already has an active membership of a different organization. " +
                "The MVP allows one at a time: revoke the existing membership first " +
                "(aids-provision revoke), then provision the new one.");
        }

        // The current operator, not the one who first provisioned the account
        // (W3-002). Dropping this actor is what made a later grant of Approver
        // attributable to whoever originally granted Member.
        active.ChangeRoles(roles, provisionedBy, clock);
    }

    /// <summary>
    /// The attribution string. The operator's own assertion plus the tool and
    /// host it came from, so a membership can be traced to a person *and* to
    /// where the command ran.
    /// </summary>
    private static string BuildProvisionedBy(string actor)
    {
        var provisionedBy = $"{actor} via aids-provision@{Environment.MachineName}";

        return provisionedBy.Length <= 256 ? provisionedBy : provisionedBy[..256];
    }
}
