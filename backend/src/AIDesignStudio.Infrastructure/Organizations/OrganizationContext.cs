using AIDesignStudio.Application.Identity;
using AIDesignStudio.Application.Organizations;
using AIDesignStudio.Domain.Organizations;
using AIDesignStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AIDesignStudio.Infrastructure.Organizations;

/// <summary>
/// Resolves the caller's organization access from the membership table, and
/// nothing else.
///
/// <para><b>The request's inputs are not consulted at all.</b> There is no
/// header, no query-string parameter, no route value, no request-body field and
/// no claim in this file. That is the point: requirement 6 of M01-03 cannot be
/// satisfied by validating untrusted input, only by never reading it. The only
/// thing taken from the request is the authenticated user id, which comes from a
/// signed session cookie the browser cannot author.</para>
///
/// <para><b>No cache beyond the request.</b> The membership is read once per
/// request and reused within it, so a revoked membership or a changed role takes
/// effect on the caller's next request with no sign-out, no cache invalidation
/// and no background job. Asserted by
/// <c>AuthorizationTests.Revoking_a_membership_removes_access_on_the_next_request</c>.
/// A longer-lived cache would be the usual way this guarantee quietly
/// disappears.</para>
/// </summary>
public sealed class OrganizationContext(
    ICurrentUser currentUser,
    AppDbContext db,
    OrganizationScope scope) : IOrganizationContext
{
    private OrganizationAccess? _access;
    private bool _resolved;

    public async Task<OrganizationAccess?> GetAccessAsync(CancellationToken cancellationToken)
    {
        if (_resolved)
        {
            return _access;
        }

        _resolved = true;

        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
        {
            return null;
        }

        // Memberships are not organization-filtered — they are what *produces*
        // the filter, so filtering them would be circular. The query is keyed on
        // the authenticated user id, which is the only thing that makes it safe.
        var membership = await db.OrganizationMemberships
            .AsNoTracking()
            .FirstOrDefaultAsync(
                m => m.UserId == userId && m.Status == MembershipStatus.Active,
                cancellationToken);

        if (membership is null)
        {
            // Authenticated, provisioned by nobody. The correct outcome is that
            // the account can reach no organization data at all (ADR-0009): the
            // scope stays closed, so every organization-scoped query returns
            // nothing even if an endpoint forgets to check this result.
            return null;
        }

        scope.SetOrganization(membership.OrganizationId);

        _access = new OrganizationAccess(membership.OrganizationId, userId, membership.Roles);

        return _access;
    }
}
