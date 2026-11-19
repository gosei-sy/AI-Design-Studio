using AIDesignStudio.Application.Organizations;

namespace AIDesignStudio.Infrastructure.Organizations;

/// <summary>
/// The request-scoped data scope. Starts closed and is opened exactly once, by
/// <see cref="OrganizationContext"/>, from a membership row.
///
/// <para>Mutable on purpose, and the mutation is deliberately not public API
/// beyond this assembly's composition: resolving a membership requires a database
/// read, and a query filter has to be able to read the answer synchronously. The
/// alternative — passing an organization id down every call — is what the
/// requirement "never trust organization values supplied by a browser" exists to
/// prevent, because a parameter threaded through forty methods eventually gets
/// its value from a route.</para>
/// </summary>
public sealed class OrganizationScope : IOrganizationScope
{
    public Guid? OrganizationId { get; private set; }

    /// <summary>Always false. An HTTP request can never become system-scoped.</summary>
    public bool IsSystem => false;

    public void SetOrganization(Guid organizationId)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException(
                "An empty organization id cannot be the request scope.", nameof(organizationId));
        }

        if (OrganizationId is { } existing && existing != organizationId)
        {
            // Nothing legitimate re-points a request at a second organization
            // mid-flight, and if something tried, every query before and after the
            // switch would be scoped differently. Refusing is the only safe
            // response.
            throw new InvalidOperationException(
                "The organization scope for this request is already set and cannot be changed.");
        }

        OrganizationId = organizationId;
    }
}

/// <summary>
/// Unscoped, by explicit request. Used by the job worker, which dispatches work
/// for every tenant, and by the operator provisioning tool, which must find an
/// organization before any membership exists to scope it.
///
/// <para>Registered only by those two hosts. The API host registers
/// <see cref="OrganizationScope"/> instead, and
/// <c>AuthorizationTests.The_api_host_is_never_system_scoped</c> asserts it.</para>
/// </summary>
public sealed class SystemOrganizationScope : IOrganizationScope
{
    public static readonly SystemOrganizationScope Instance = new();

    public Guid? OrganizationId => null;

    public bool IsSystem => true;
}

/// <summary>
/// A fixed organization scope. Exists for tests and for the provisioning tool's
/// verification read, where the organization is already known and no membership
/// is involved.
/// </summary>
public sealed class FixedOrganizationScope(Guid organizationId) : IOrganizationScope
{
    public Guid? OrganizationId { get; } = organizationId;

    public bool IsSystem => false;
}

/// <summary>
/// The closed scope: no organization, not system. Nothing organization-scoped is
/// visible. This is what a context built without a scope gets.
/// </summary>
public sealed class ClosedOrganizationScope : IOrganizationScope
{
    public static readonly ClosedOrganizationScope Instance = new();

    public Guid? OrganizationId => null;

    public bool IsSystem => false;
}
