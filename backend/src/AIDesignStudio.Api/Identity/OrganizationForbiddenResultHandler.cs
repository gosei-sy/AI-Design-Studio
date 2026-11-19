using AIDesignStudio.Application.Organizations;
using AIDesignStudio.Domain.Organizations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace AIDesignStudio.Api.Identity;

/// <summary>
/// Says <em>why</em> an organization policy refused a caller (M01-05).
///
/// <para>The role policies decide access; left to the framework, a refusal is a
/// bare 403 with no code. The studio then cannot tell "nobody provisioned this
/// account" from "you are a Member, and this needs an Approver" — and those send
/// a person to different places. This writes the same coded problem document the
/// handlers already return, so a policy is as informative as a hand-written
/// check and there is no reason to prefer the latter.</para>
///
/// <para>Only organization-role refusals are rewritten. Everything else — the
/// 401 for no session above all — goes to the framework's handler untouched, so
/// this cannot change who is let in. It reports on a decision; it does not make
/// one.</para>
/// </summary>
internal sealed class OrganizationForbiddenResultHandler(IOrganizationContext organizations)
    : IAuthorizationMiddlewareResultHandler
{
    private static readonly AuthorizationMiddlewareResultHandler Default = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        var refused = authorizeResult.Forbidden
            ? authorizeResult.AuthorizationFailure?.FailedRequirements
                .OfType<OrganizationRoleRequirement>()
                .FirstOrDefault()
            : null;

        if (refused is null)
        {
            await Default.HandleAsync(next, context, policy, authorizeResult);
            return;
        }

        // Already resolved by the policy handler in this request; this reads the
        // cached answer rather than the database.
        var access = await organizations.GetAccessAsync(context.RequestAborted);

        var problem = access is null
            ? OrganizationProblems.NoMembership()
            : refused.Required.HasFlag(OrganizationRoles.Approver) && !access.IsApprover
                ? OrganizationProblems.ApproverRequired()
                : OrganizationProblems.MemberRequired();

        await problem.ExecuteAsync(context);
    }
}

/// <summary>The coded 403s for organization access, in one place so every surface gives the same ones.</summary>
internal static class OrganizationProblems
{
    /// <summary>
    /// Authenticated, but provisioned by nobody. Explained rather than left as a
    /// bare 403: the caller is asking about their own account, so there is no
    /// enumeration risk, and "ask an operator" is the only useful next step.
    /// </summary>
    public static IResult NoMembership() => Forbidden(
        "authz.no_organization_membership",
        "No organization membership",
        "This account is not a member of any organization. Access is granted by a trusted " +
        "operator (decision D-9); registering an account does not grant it.");

    public static IResult MemberRequired() => Forbidden(
        "authz.member_required",
        "Member role required",
        "Authoring needs the Member role. This account holds Approver only in its organization; " +
        "an operator can add Member.");

    public static IResult ApproverRequired() => Forbidden(
        "authz.approver_required",
        "Approver role required",
        "Only an Approver may approve, reject or waive. This account is a Member of the " +
        "organization but does not hold the Approver role.");

    private static IResult Forbidden(string code, string title, string detail) => Results.Problem(
        type: $"https://aidesignstudio.dev/problems/{code}",
        title: title,
        statusCode: StatusCodes.Status403Forbidden,
        detail: detail,
        extensions: new Dictionary<string, object?> { ["code"] = code });
}
