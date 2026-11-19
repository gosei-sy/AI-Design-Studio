using Microsoft.AspNetCore.Identity;

namespace AIDesignStudio.Infrastructure.Identity;

/// <summary>
/// A role record. The table exists in M01-02 because Identity's store requires
/// it, but **no role is seeded and no role is enforced here.**
///
/// Decision D-3 collapses the four-role model to two for the MVP — **Member** and
/// **Approver**, a user may hold both, and only Approver may perform
/// approval, rejection or waiver transitions. Enforcing that is authorization,
/// which is **M01-03**, and M01-03 must not begin until M01-02 is independently
/// accepted. Seeding the roles or adding a policy here would start it.
///
/// See <c>docs/02-architecture/decisions/ADR-0008-authentication-is-local-identity-with-cookie-sessions.md</c>.
/// </summary>
public sealed class ApplicationRole : IdentityRole<Guid>
{
    public ApplicationRole()
    {
        Id = Guid.CreateVersion7();
    }

    public ApplicationRole(string name)
        : this()
    {
        Name = name;
    }
}
