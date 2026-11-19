using System.Security.Claims;
using AIDesignStudio.Application.Identity;
using Microsoft.AspNetCore.Http;

namespace AIDesignStudio.Api.Identity;

/// <summary>
/// Reads the authenticated caller from the ambient <c>HttpContext</c>.
///
/// Lives in the API host because an <c>HttpContext</c> is an HTTP concern and
/// the host is the only project with the ASP.NET Core framework reference.
/// Infrastructure owns the Identity *store*; the host owns the session. The
/// worker has no HTTP pipeline and never resolves this.
/// </summary>
public sealed class HttpContextCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public Guid? UserId =>
        Guid.TryParse(Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public string? Email => Principal?.FindFirstValue(ClaimTypes.Email);

    /// <summary>
    /// The immutable user id.
    ///
    /// W2-005: this returned the email address, which is self-asserted because
    /// registration is public and confirmation is disabled — so registering
    /// someone else's address would have recorded you as them in every approval.
    /// A reviewer still needs a readable name on screen, but that is a join at
    /// read time, not the value the audit trail is keyed on.
    /// </summary>
    public string RequireAuditIdentity()
    {
        if (!IsAuthenticated)
        {
            throw new InvalidOperationException(
                "No authenticated user is available. An audit identity was requested outside an " +
                "authenticated request, which means an endpoint is missing its authorization " +
                "requirement — see S-19 and S-21.");
        }

        return UserId?.ToString()
            ?? throw new InvalidOperationException(
                "The authenticated principal carries no name identifier claim, so no stable audit " +
                "subject exists. An email address is not an acceptable substitute (W2-005).");
    }
}
