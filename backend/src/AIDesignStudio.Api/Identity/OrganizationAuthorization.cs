using AIDesignStudio.Application.Organizations;
using AIDesignStudio.Domain.Organizations;
using Microsoft.AspNetCore.Authorization;

namespace AIDesignStudio.Api.Identity;

/// <summary>Policy names for organization-scoped authorization (D-3, ADR-0009).</summary>
internal static class OrganizationPolicies
{
    /// <summary>Any active membership: the caller belongs to an organization.</summary>
    public const string Membership = "organization.membership";

    /// <summary>
    /// The Approver role. This is the policy that approve, reject and waive must
    /// carry when those endpoints are built (M01-05).
    /// </summary>
    public const string Approver = "organization.approver";
}

/// <summary>
/// Requires an active membership carrying every role in
/// <paramref name="required"/>.
/// </summary>
internal sealed class OrganizationRoleRequirement(OrganizationRoles required) : IAuthorizationRequirement
{
    public OrganizationRoles Required { get; } = required;
}

/// <summary>
/// Decides the requirement from **server-side membership state**.
///
/// <para>Note what this handler does not do: it never looks at
/// <c>context.User</c> for a role. ASP.NET Core's own
/// <c>RequireRole("Approver")</c> would read a role claim, and Identity's claims
/// principal factory populates role claims from <c>auth.user_roles</c> — which is
/// a **global** role table. An Approver there would be an Approver of every
/// organization. Reading membership instead is what makes the role
/// organization-scoped, and
/// <c>AuthorizationTests.A_global_identity_role_named_Approver_grants_nothing</c>
/// fails if anyone changes it back.</para>
/// </summary>
internal sealed class OrganizationRoleHandler(
    IOrganizationContext organizations,
    IHttpContextAccessor httpContextAccessor)
    : AuthorizationHandler<OrganizationRoleRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, OrganizationRoleRequirement requirement)
    {
        var cancellationToken = httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None;

        var access = await organizations.GetAccessAsync(cancellationToken);

        // No membership is a failure, not an exception. An authenticated account
        // that nobody provisioned is the expected state of a self-registered
        // Development account (ADR-0009), and the correct answer is 403.
        if (access is null)
        {
            return;
        }

        if ((access.Roles & requirement.Required) == requirement.Required)
        {
            context.Succeed(requirement);
        }
    }
}

internal static class OrganizationAuthorizationExtensions
{
    public static AuthorizationOptions AddOrganizationPolicies(this AuthorizationOptions options)
    {
        options.AddPolicy(OrganizationPolicies.Membership, policy => policy
            .RequireAuthenticatedUser()
            .AddRequirements(new OrganizationRoleRequirement(OrganizationRoles.Member)));

        options.AddPolicy(OrganizationPolicies.Approver, policy => policy
            .RequireAuthenticatedUser()
            .AddRequirements(new OrganizationRoleRequirement(OrganizationRoles.Approver)));

        return options;
    }
}
