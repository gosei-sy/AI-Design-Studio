using AIDesignStudio.Domain.Organizations;
using AIDesignStudio.SharedKernel;

namespace AIDesignStudio.Application.Organizations;

/// <summary>
/// What the current caller may do, **resolved from server-side membership state
/// and from nothing else**.
///
/// <para>Every field here comes from a row in <c>auth.organization_memberships</c>
/// that an operator created. No part of it is read from a header, a query string,
/// a route value, a request body, a cookie payload or a role claim — see
/// ADR-0009. This type exists so that the one place authorization is decided is
/// also the one place it can be inspected in a test.</para>
/// </summary>
/// <param name="OrganizationId">The organization the caller is acting in.</param>
/// <param name="UserId">The caller's immutable user id.</param>
/// <param name="Roles">The roles the membership carries, inside that organization only.</param>
public sealed record OrganizationAccess(Guid OrganizationId, Guid UserId, OrganizationRoles Roles)
{
    public bool IsApprover => Roles.HasFlag(OrganizationRoles.Approver);

    public bool IsMember => Roles.HasFlag(OrganizationRoles.Member);

    /// <summary>
    /// The guard for approve, reject and waive (D-3).
    ///
    /// <para>Throws rather than returning false, so a caller cannot forget to
    /// branch on the answer. A missing <c>if</c> is the most common way an
    /// authorization check becomes decorative — W2-001 and W2-RR-002 were both
    /// that exact shape: a value computed and never consulted.</para>
    /// </summary>
    public void RequireApprover()
    {
        if (!IsApprover)
        {
            throw new DomainException(
                "authz.approver_required",
                "Only an Approver may approve, reject or waive. This account is a Member of the " +
                "organization but does not hold the Approver role.");
        }
    }
}
