using AIDesignStudio.Api.Identity;
using AIDesignStudio.Application.Organizations;
using AIDesignStudio.Domain.Organizations;
using AIDesignStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AIDesignStudio.Api.Endpoints;

/// <summary>
/// The organization-scoped surface for M01-03. Three endpoints, and every one of
/// them answers from server-side membership state.
///
/// <para><b>Nothing here reads an organization or a role from the request.</b>
/// No header, no query-string parameter, no request-body field, no claim. The
/// route parameter on <c>GET /organizations/{id}</c> is the one client-supplied
/// organization id in the file, and it is used only as something to *compare*
/// against the resolved scope — never to establish it. That distinction is the
/// requirement: input may ask a question, it may not answer one.</para>
///
/// <para><b>Why a cross-organization read is 404 and not 403.</b> A 403 confirms
/// the organization exists and that the caller is simply not in it, which turns
/// the endpoint into a tenant-existence oracle — the same class of leak as the
/// account-enumeration oracles corrected in W2-002. 404 is the honest answer to
/// "does this resource exist, as far as you are concerned?", and it is identical
/// whether the organization exists or not.</para>
/// </summary>
internal static class OrganizationEndpoints
{
    public static IEndpointRouteBuilder MapOrganizationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/organizations")
            .WithTags("Organizations")
            .AddEndpointFilter<RequestValidationEndpointFilter>();

        // --- The caller's own organization ------------------------------------
        group.MapGet("/current", async (
                IOrganizationContext organizations,
                AppDbContext db,
                CancellationToken cancellationToken) =>
            {
                var access = await organizations.GetAccessAsync(cancellationToken);

                if (access is null)
                {
                    return NoMembership();
                }

                // Safe without an explicit organization predicate *because* the id
                // comes from the resolved membership rather than from the request.
                var organization = await db.Organizations.AsNoTracking()
                    .FirstOrDefaultAsync(o => o.Id == access.OrganizationId, cancellationToken);

                return organization is null
                    ? NoMembership()
                    : Results.Ok(Describe(organization, access.Roles));
            })
            .WithSummary("The organization the caller is a member of, resolved from membership state.");

        // --- A specific organization, by id -----------------------------------
        group.MapGet("/{organizationId:guid}", async (
                Guid organizationId,
                IOrganizationContext organizations,
                AppDbContext db,
                CancellationToken cancellationToken) =>
            {
                var access = await organizations.GetAccessAsync(cancellationToken);

                if (access is null)
                {
                    return NoMembership();
                }

                if (organizationId != access.OrganizationId)
                {
                    // Refused before any query runs. Nothing about the requested
                    // organization is read, so the response cannot vary with
                    // whether it exists.
                    return NotFound();
                }

                var organization = await db.Organizations.AsNoTracking()
                    .FirstOrDefaultAsync(o => o.Id == organizationId, cancellationToken);

                return organization is null
                    ? NotFound()
                    : Results.Ok(Describe(organization, access.Roles));
            })
            .WithSummary("Reads one organization. Any organization but the caller's own is 404.");

        // --- Who may approve, for an approver ---------------------------------
        group.MapGet("/current/members", async (
                IOrganizationContext organizations,
                AppDbContext db,
                CancellationToken cancellationToken) =>
            {
                // The policy already proved an Approver membership exists, so a
                // null here would be a wiring fault rather than a user error.
                var access = await organizations.GetAccessAsync(cancellationToken)
                    ?? throw new InvalidOperationException(
                        "The Approver policy succeeded without a resolvable membership.");

                // **The explicit predicate matters.** Memberships are the one
                // organization-bearing table with no global query filter — they
                // are what produces the filter, so filtering them would be
                // circular. That makes this the single query in the application
                // where tenant scoping is the author's responsibility rather than
                // the model's, which is why
                // An_approver_cannot_list_another_organizations_members exists.
                var members = await db.OrganizationMemberships.AsNoTracking()
                    .Where(m => m.OrganizationId == access.OrganizationId
                        && m.Status == MembershipStatus.Active)
                    .OrderBy(m => m.CreatedAt)
                    .Select(m => new
                    {
                        userId = m.UserId,
                        roles = m.Roles,
                        provisionedBy = m.ProvisionedBy,
                        provisionedAt = m.CreatedAt,

                        // W3-002. Returning only provisionedBy made this endpoint
                        // misattribute a promotion: after operator A granted
                        // Member and operator B granted Approver, the roster still
                        // named A beside the Approver role. Both are reported, and
                        // the full chain is in auth.organization_membership_events.
                        rolesChangedBy = m.RolesChangedBy,
                        rolesChangedAt = m.RolesChangedAt,
                    })
                    .ToListAsync(cancellationToken);

                // Ids and roles, not email addresses. An address is self-asserted
                // (W2-005) so it would be misleading as an identity, and an
                // approver does not need it to know who holds approval authority.
                return Results.Ok(new
                {
                    organizationId = access.OrganizationId,
                    members = members.Select(m => new
                    {
                        m.userId,
                        roles = RoleNames(m.roles),
                        m.provisionedBy,
                        m.provisionedAt,
                        m.rolesChangedBy,
                        m.rolesChangedAt,
                    }),
                });
            })
            .RequireAuthorization(OrganizationPolicies.Approver)
            .WithSummary("Lists active memberships of the caller's organization. Approver only.");

        return app;
    }

    private static object Describe(Organization organization, OrganizationRoles roles) => new
    {
        organizationId = organization.Id,
        name = organization.Name,
        roles = RoleNames(roles),
    };

    private static string[] RoleNames(OrganizationRoles roles) =>
        [.. new[] { OrganizationRoles.Member, OrganizationRoles.Approver }
            .Where(role => roles.HasFlag(role))
            .Select(role => role.ToString())];

    /// <summary>
    /// Authenticated, but provisioned by nobody. Explained rather than left as a
    /// bare 403: the caller is asking about their own account, so there is no
    /// enumeration risk, and "ask an operator" is the only useful next step.
    /// </summary>
    private static IResult NoMembership() => Results.Problem(
        type: "https://aidesignstudio.dev/problems/authz.no_organization_membership",
        title: "No organization membership",
        statusCode: StatusCodes.Status403Forbidden,
        detail: "This account is not a member of any organization. Access is granted by a trusted " +
                "operator (decision D-9); registering an account does not grant it.",
        extensions: new Dictionary<string, object?>
        {
            ["code"] = "authz.no_organization_membership",
        });

    private static IResult NotFound() => Results.Problem(
        type: "https://aidesignstudio.dev/problems/organization.not_found",
        title: "Organization not found",
        statusCode: StatusCodes.Status404NotFound,
        detail: "No such organization.",
        extensions: new Dictionary<string, object?> { ["code"] = "organization.not_found" });
}
