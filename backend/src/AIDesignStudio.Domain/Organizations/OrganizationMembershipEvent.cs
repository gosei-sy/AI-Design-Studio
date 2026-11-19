using AIDesignStudio.SharedKernel;

namespace AIDesignStudio.Domain.Organizations;

/// <summary>What happened to a membership.</summary>
public enum MembershipEventType
{
    /// <summary>The membership was created, with its initial roles.</summary>
    Provisioned = 1,

    /// <summary>The role set changed on an existing membership.</summary>
    RolesChanged = 2,

    /// <summary>Access was withdrawn.</summary>
    Revoked = 3,

    /// <summary>
    /// The state **observed at migration time** for a membership that predates
    /// the audit chain (W3-RR-002). Written only by the upgrade migration; the
    /// aggregate can never produce it, which
    /// <c>OrganizationMembershipTests.The_aggregate_never_produces_a_migration_baseline</c>
    /// asserts.
    ///
    /// <para><b>It is not a provisioning event.</b> Its <c>Actor</c> names the
    /// migration, not the operator, and its <c>OccurredAt</c> is when the
    /// migration ran, not when the grant happened. The original attribution is
    /// still on the membership row in <c>ProvisionedBy</c> / <c>CreatedAt</c>,
    /// recorded separately. Combining those with the current role set into one
    /// entry would assert that the person held *these* roles at *that* time,
    /// which is unknowable for any membership whose roles changed before the
    /// upgrade — a fabrication the Product Owner's correction removed from the
    /// first design.</para>
    ///
    /// <para><c>PreviousRoles</c> is therefore null: what came before is not
    /// recoverable.</para>
    /// </summary>
    MigrationBaseline = 4,
}

/// <summary>
/// One entry in a membership's append-only audit chain (W3-002).
///
/// <para><b>Why this exists.</b> The first implementation recorded
/// <c>ProvisionedBy</c> at creation and nothing afterwards, so when operator A
/// granted Member and operator B later granted **Approver**, the durable state
/// still named A. Granting approval authority is a privileged change, and a log
/// line is not an audit record: nobody designed the log's retention, integrity
/// or correlation, and the authoritative row said something false. The
/// independent review was right to call it a P1.</para>
///
/// <para><b>Append-only, and enforced by the aggregate.</b> Events are an owned
/// collection of <see cref="OrganizationMembership"/>, which exposes them
/// read-only and offers no way to remove or edit one. Every state change on the
/// aggregate writes an event in the same operation, so "a role changed but
/// nothing recorded it" is not a reachable state. That is an aggregate-level
/// guarantee, not a database one: a writer with table privileges could still
/// delete a row, and nothing here claims otherwise.</para>
///
/// <para>Each entry is **self-contained** — organization and user are stored on
/// it rather than only reachable through the membership — so an auditor reading
/// this table alone can answer who changed whose access, in which tenant,
/// when.</para>
/// </summary>
public sealed class OrganizationMembershipEvent : Entity
{
    private OrganizationMembershipEvent() { } // EF Core

    public Guid MembershipId { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid UserId { get; private set; }

    public MembershipEventType Type { get; private set; }

    /// <summary>The role set before this event; null for <c>Provisioned</c>.</summary>
    public OrganizationRoles? PreviousRoles { get; private set; }

    /// <summary>The role set after this event; null for <c>Revoked</c>.</summary>
    public OrganizationRoles? NewRoles { get; private set; }

    /// <summary>
    /// Who performed it. Required for every event type — the whole point of the
    /// chain is that no privileged change is unattributable.
    /// </summary>
    public string Actor { get; private set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; private set; }

    internal static OrganizationMembershipEvent Record(
        OrganizationMembership membership,
        MembershipEventType type,
        OrganizationRoles? previousRoles,
        OrganizationRoles? newRoles,
        string actor,
        IClock clock) => new()
        {
            MembershipId = membership.Id,
            OrganizationId = membership.OrganizationId,
            UserId = membership.UserId,
            Type = type,
            PreviousRoles = previousRoles,
            NewRoles = newRoles,
            Actor = actor,
            OccurredAt = clock.UtcNow,
        };
}
