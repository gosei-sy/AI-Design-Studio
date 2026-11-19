namespace AIDesignStudio.Application.Identity;

/// <summary>
/// The authenticated caller, as the application layer sees them.
///
/// This exists so the domain can record *who* approved, rejected or waived
/// something without any layer below the host knowing what a cookie, a claim or
/// an <c>HttpContext</c> is. The architecture tests forbid
/// <c>Microsoft.AspNetCore</c> in Domain and Application, and this interface is
/// how authentication crosses that boundary: a plain contract here, an
/// <c>HttpContext</c> reader in the API host.
///
/// <para>Deliberately minimal, and it stayed that way through M01-03. It answers
/// "who is calling" and nothing else — no roles, no organization. Authorization
/// lives behind <see cref="Organizations.IOrganizationContext"/>, which needs a
/// database read; this one does not. Keeping them apart means the audit identity
/// cannot start depending on authorization state, and it leaves one place to
/// point a test at for each question (ADR-0009).</para>
/// </summary>
public interface ICurrentUser
{
    /// <summary>True when the request carries a valid authenticated session.</summary>
    bool IsAuthenticated { get; }

    /// <summary>The user's stable identifier, or null when unauthenticated.</summary>
    Guid? UserId { get; }

    /// <summary>The user's email address, or null when unauthenticated.</summary>
    string? Email { get; }

    /// <summary>
    /// The value to persist in audit fields such as <c>ApprovedBy</c> and
    /// <c>WaivedBy</c>: the **immutable user id**.
    ///
    /// <para>Not the email address. W2-005: registration is public and email
    /// confirmation is disabled, so an address is self-asserted — anyone can
    /// register someone else's and would then have been recorded as that person
    /// in every future approval. An id cannot be claimed, cannot be changed, and
    /// cannot be re-pointed at a different human by editing a profile field.
    /// Email is display metadata; <see cref="Email"/> exposes it for that.</para>
    ///
    /// <para>Throws rather than returning a placeholder when unauthenticated: an
    /// approval attributed to "system" or an empty string is an audit trail that
    /// lies. Callers reach this only behind an authenticated endpoint, so throwing
    /// means a wiring mistake, not a user error.</para>
    ///
    /// <para><b>What makes the identity *proved* is not here.</b> This id is
    /// stable and attributable on its own; whether the human behind the account is
    /// who they claim was decision **D-9**, resolved by ADR-0009 as trusted
    /// operator provisioning. An account that nobody provisioned holds no
    /// authority at all, so there is nothing of consequence to attribute to it —
    /// and an account that was provisioned carries the operator's recorded
    /// assertion on its membership row.</para>
    /// </summary>
    string RequireAuditIdentity();
}
