using AIDesignStudio.SharedKernel;

namespace AIDesignStudio.Domain.Organizations;

/// <summary>
/// What a user may do inside an organization. **Organization-scoped, not global**
/// (D-3: two roles for the MVP — Member and Approver).
///
/// <para>A flags enum rather than a collection of rows, because a user holds a
/// subset of exactly two roles and the set is closed. It also makes "at least one
/// role" expressible as a database check constraint — <c>roles &gt; 0</c> — which a
/// child table could not do.</para>
///
/// <para><b>These are not ASP.NET Core Identity roles.</b> Identity roles are
/// global: a row in <c>auth.user_roles</c> would make someone an Approver of
/// everything. The whole point of D-3 and ADR-0009 is that approval authority
/// belongs to a membership of one organization. Nothing in the authorization path
/// reads a role claim — see
/// <c>AuthorizationTests.A_global_identity_role_named_Approver_grants_nothing</c>.</para>
/// </summary>
[Flags]
public enum OrganizationRoles
{
    None = 0,

    /// <summary>May read and author inside the organization.</summary>
    Member = 1,

    /// <summary>May additionally approve, reject or waive.</summary>
    Approver = 2,
}

/// <summary>Whether a membership currently grants anything.</summary>
public enum MembershipStatus
{
    Active = 1,

    /// <summary>
    /// Kept rather than deleted. A revoked membership is the audit record of who
    /// had access and who removed it; deleting the row would erase that.
    /// </summary>
    Revoked = 2,
}

/// <summary>
/// A user's membership of one organization, with the roles it carries.
///
/// <para><b>Modelled as its own aggregate, not as columns on the user</b>
/// (ADR-0009). The MVP allows exactly one *active* membership per user, which a
/// column would also express — but moving to several later would then mean
/// migrating organization and role data out of the authorization boundary, which
/// is the migration this shape exists to avoid. The single-active rule is a
/// filtered unique index, not a table shape, so relaxing it is a migration that
/// drops an index.</para>
///
/// <para><b>Membership is the only source of authorization.</b> It is created by
/// trusted operator provisioning and by nothing else: no HTTP endpoint, no
/// self-registration path, and no client-supplied header, claim, query string or
/// request field can produce one. That is what "proofing" means here — the
/// operator's recorded assertion that the person was verified out of band, which
/// is why <see cref="ProvisionedBy"/> is required rather than optional.</para>
/// </summary>
public sealed class OrganizationMembership : AggregateRoot
{
    /// <summary>
    /// Every bit the model knows about. Anything outside this mask is a value
    /// from a future version or a cast integer, and both are rejected — in the
    /// aggregate and again by <c>ck_organization_memberships_roles</c>.
    /// </summary>
    public const OrganizationRoles KnownRoles = OrganizationRoles.Member | OrganizationRoles.Approver;

    private readonly List<OrganizationMembershipEvent> _events = [];

    private OrganizationMembership() { } // EF Core

    public Guid OrganizationId { get; private set; }

    /// <summary>
    /// The **immutable user id** from the authenticated session — never the email
    /// address. W2-005: registration is public in Development and confirmation is
    /// disabled, so an address is self-asserted and could be re-pointed at a
    /// different human by editing a profile field. A membership keyed on an
    /// address would hand over that person's organization access with it.
    /// </summary>
    public Guid UserId { get; private set; }

    public OrganizationRoles Roles { get; private set; }

    public MembershipStatus Status { get; private set; }

    /// <summary>
    /// Who or what performed the provisioning — the operator's recorded
    /// assertion. Required, because an unattributable grant of access is the
    /// thing ADR-0009 exists to prevent.
    /// </summary>
    public string ProvisionedBy { get; private set; } = string.Empty;

    /// <summary>
    /// Who last changed the role set, and when. Null until a role change happens
    /// (W3-002).
    ///
    /// <para>Separate from <see cref="ProvisionedBy"/> rather than overwriting it:
    /// "who first granted this person access" and "who last changed what they may
    /// do" are different questions, and a privileged change that silently
    /// replaced the original attribution would destroy the answer to the first
    /// one. The full sequence is in <see cref="Events"/>; these two columns exist
    /// so the most recent answer is visible on the row itself, without a join —
    /// which is how an operator reads it in <c>psql</c> and how the API returns
    /// it.</para>
    /// </summary>
    public DateTimeOffset? RolesChangedAt { get; private set; }

    public string? RolesChangedBy { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public string? RevokedBy { get; private set; }

    /// <summary>
    /// The append-only audit chain: every grant, role change and revocation, in
    /// order, each naming the operator who performed it.
    ///
    /// <para>Read-only by construction. The aggregate writes an entry in the same
    /// operation as the state change it records, so there is no reachable state
    /// where a role changed and nothing recorded it.</para>
    /// </summary>
    public IReadOnlyList<OrganizationMembershipEvent> Events => _events.AsReadOnly();

    public bool IsActive => Status == MembershipStatus.Active;

    /// <summary>
    /// True only for an **active** membership carrying Approver. A revoked
    /// membership answers false to everything, so revocation does not need to
    /// also clear the roles.
    /// </summary>
    public bool CanApprove => IsActive && Roles.HasFlag(OrganizationRoles.Approver);

    public bool HasRole(OrganizationRoles role) => IsActive && role != OrganizationRoles.None
        && (Roles & role) == role;

    public static OrganizationMembership Provision(
        Guid organizationId,
        Guid userId,
        OrganizationRoles roles,
        string provisionedBy,
        IClock clock)
    {
        if (organizationId == Guid.Empty)
        {
            throw new DomainException(
                "membership.organization_required", "A membership must belong to an organization.");
        }

        if (userId == Guid.Empty)
        {
            throw new DomainException(
                "membership.user_required", "A membership must belong to a user.");
        }

        if (string.IsNullOrWhiteSpace(provisionedBy))
        {
            throw new DomainException(
                "membership.provisioner_required",
                "A membership must record who provisioned it. Access that cannot be attributed " +
                "cannot be audited or withdrawn with confidence.");
        }

        var actor = provisionedBy.Trim();

        var membership = new OrganizationMembership
        {
            OrganizationId = organizationId,
            UserId = userId,
            Roles = ValidateRoles(roles),
            Status = MembershipStatus.Active,
            ProvisionedBy = actor,
            CreatedAt = clock.UtcNow,
        };

        membership._events.Add(OrganizationMembershipEvent.Record(
            membership, MembershipEventType.Provisioned,
            previousRoles: null, newRoles: membership.Roles, actor, clock));

        return membership;
    }

    /// <summary>
    /// Replaces the role set wholesale — the operation re-provisioning performs.
    ///
    /// <para>Setting it to none is refused rather than treated as "remove all
    /// access". A membership with no roles would grant nothing while still
    /// occupying the one active slot per user, so the account would be
    /// unprovisionable until somebody noticed the empty row. Withdrawing access
    /// is <see cref="Revoke"/>, which says so in the data.</para>
    /// </summary>
    /// <param name="changedBy">
    /// Who is making the change. **Required** — W3-002: the first version took no
    /// actor, so a later operator granting Approver left the row still naming
    /// whoever originally granted Member. Granting approval authority is a
    /// privileged change, and the durable state said something false about who
    /// made it.
    /// </param>
    public void ChangeRoles(OrganizationRoles roles, string changedBy, IClock clock)
    {
        RequireActive();

        if (string.IsNullOrWhiteSpace(changedBy))
        {
            throw new DomainException(
                "membership.role_changer_required",
                "A role change must record who performed it. Granting or removing approval " +
                "authority is a privileged change and may not be anonymous.");
        }

        var previousRoles = Roles;
        var actor = changedBy.Trim();

        Roles = ValidateRoles(roles);
        RolesChangedBy = actor;
        RolesChangedAt = clock.UtcNow;

        _events.Add(OrganizationMembershipEvent.Record(
            this, MembershipEventType.RolesChanged, previousRoles, Roles, actor, clock));

        Touch(clock);
    }

    public void Revoke(string revokedBy, IClock clock)
    {
        if (string.IsNullOrWhiteSpace(revokedBy))
        {
            throw new DomainException(
                "membership.revoker_required", "A revocation must record who performed it.");
        }

        if (Status == MembershipStatus.Revoked)
        {
            throw new DomainException(
                "membership.already_revoked",
                "This membership is already revoked.");
        }

        var actor = revokedBy.Trim();

        Status = MembershipStatus.Revoked;
        RevokedBy = actor;
        RevokedAt = clock.UtcNow;

        _events.Add(OrganizationMembershipEvent.Record(
            this, MembershipEventType.Revoked, previousRoles: Roles, newRoles: null, actor, clock));

        Touch(clock);
    }

    private void RequireActive()
    {
        if (Status != MembershipStatus.Active)
        {
            throw new DomainException(
                "membership.revoked",
                "A revoked membership cannot be modified. Provision a new one instead.");
        }
    }

    private static OrganizationRoles ValidateRoles(OrganizationRoles roles)
    {
        if (roles == OrganizationRoles.None)
        {
            throw new DomainException(
                "membership.role_required",
                "A membership must carry at least one role: Member, Approver, or both.");
        }

        // Enum.IsDefined is the wrong check for a flags enum: it rejects the
        // legitimate Member|Approver combination and accepts nothing useful. A
        // mask test is what actually catches (OrganizationRoles)999.
        if ((roles & ~KnownRoles) != OrganizationRoles.None)
        {
            throw new DomainException(
                "membership.invalid_role",
                $"'{(int)roles}' contains roles this application does not define. Valid roles are " +
                "Member and Approver.");
        }

        return roles;
    }
}
