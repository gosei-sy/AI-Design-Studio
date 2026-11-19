using Microsoft.AspNetCore.Identity;

namespace AIDesignStudio.Infrastructure.Identity;

/// <summary>
/// The persisted account. Lives in Infrastructure because it is an ASP.NET Core
/// Identity type backed by EF Core, and the architecture tests keep both out of
/// Domain and Application.
///
/// <para><b>Still no organization membership and no role, after M01-03.</b> That
/// is the decision, not an omission: ADR-0009 models membership as its own
/// aggregate in <c>auth.organization_memberships</c> rather than as a column
/// here, so that allowing several organizations later is a change to an index
/// instead of a migration of authorization data out of the user table. The domain
/// never sees this type; it receives a string through
/// <see cref="Application.Identity.ICurrentUser"/>.</para>
/// </summary>
public sealed class ApplicationUser : IdentityUser<Guid>
{
    public ApplicationUser()
    {
        // A UUID v7 assigned here rather than by the database, matching
        // SharedKernel.Entity's strategy so the whole schema has one id story.
        // Identity's own constructor leaves a Guid key at default, and
        // AppDbContext declares domain ids as never store-generated — so without
        // this, every insert would attempt Guid.Empty and the second account
        // would collide on the primary key.
        Id = Guid.CreateVersion7();
        SecurityStamp = Guid.CreateVersion7().ToString();
    }

    public ApplicationUser(string email)
        : this()
    {
        UserName = email;
        Email = email;
    }
}
