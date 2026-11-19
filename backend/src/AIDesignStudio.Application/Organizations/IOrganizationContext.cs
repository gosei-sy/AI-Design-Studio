namespace AIDesignStudio.Application.Organizations;

/// <summary>
/// Resolves the current caller's organization access, and applies it as the
/// query scope for the rest of the request.
///
/// <para><b>Why this is a port rather than a property on
/// <see cref="Identity.ICurrentUser"/>.</b> Answering "who is calling" needs no
/// database; answering "what may they do" needs a membership read. Keeping them
/// apart means the audit identity cannot accidentally start depending on
/// authorization state, and it keeps the authorization read in one place that a
/// test can point at.</para>
///
/// <para><b>Fail-closed.</b> Until something calls this, the data scope for the
/// request is "no organization", and every organization-scoped query returns
/// nothing. An endpoint that forgets to ask therefore sees an empty database
/// rather than everyone's rows — see <see cref="IOrganizationScope"/>.</para>
/// </summary>
public interface IOrganizationContext
{
    /// <summary>
    /// The caller's access, or <c>null</c> when they are unauthenticated or have
    /// no active membership.
    ///
    /// <para>An authenticated account with no membership is the normal state of a
    /// freshly self-registered Development account: it exists, it can sign in,
    /// and it can reach nothing (ADR-0009). That is a successful outcome of the
    /// design, not an error, which is why this returns null rather than
    /// throwing.</para>
    /// </summary>
    Task<OrganizationAccess?> GetAccessAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The data scope in force for the current unit of work. Read by the persistence
/// layer's global query filters, so it decides which rows exist as far as the
/// rest of the request is concerned.
///
/// <para>Three states, and the default is the closed one:</para>
///
/// <list type="bullet">
/// <item><b>None</b> — the initial state. No organization-scoped row matches
/// anything. A forgotten authorization check produces an empty result set, not a
/// cross-tenant read.</item>
/// <item><b>An organization</b> — set from a resolved membership, by
/// <see cref="IOrganizationContext"/> only.</item>
/// <item><b>System</b> — unscoped. Reachable only by a process that asks for it
/// explicitly: the worker, which dispatches jobs for every tenant, and the
/// operator provisioning tool, which must see an organization before any
/// membership exists. Never reachable from an HTTP request.</item>
/// </list>
/// </summary>
public interface IOrganizationScope
{
    /// <summary>The organization in force, or null for None and System.</summary>
    Guid? OrganizationId { get; }

    /// <summary>True when the scope is unscoped-by-design.</summary>
    bool IsSystem { get; }
}
