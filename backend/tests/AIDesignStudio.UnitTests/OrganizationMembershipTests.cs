using AIDesignStudio.Application.Organizations;
using AIDesignStudio.Domain.Organizations;
using AIDesignStudio.SharedKernel;
using Shouldly;
using Xunit;

namespace AIDesignStudio.UnitTests;

/// <summary>
/// The invariants of the authorization aggregate (D-9, ADR-0009).
///
/// <para>Each of these is also a database constraint. The aggregate is where the
/// operator gets a readable error; the database is where the guarantee survives a
/// code path that never loaded the aggregate. Both are tested — here, and in
/// <c>AuthorizationTests</c>'s constraint cases.</para>
/// </summary>
public sealed class OrganizationMembershipTests
{
    private static readonly Guid AnOrganization = Guid.CreateVersion7();
    private static readonly Guid AUser = Guid.CreateVersion7();

    private static OrganizationMembership Provision(
        OrganizationRoles roles = OrganizationRoles.Member, IClock? clock = null) =>
        OrganizationMembership.Provision(
            AnOrganization, AUser, roles, "ops@example.com", clock ?? new TestClock());

    // -----------------------------------------------------------------------
    // Provisioning
    // -----------------------------------------------------------------------

    [Fact]
    public void A_membership_records_its_organization_user_roles_and_provisioner()
    {
        var clock = new TestClock();

        var membership = OrganizationMembership.Provision(
            AnOrganization, AUser, OrganizationRoles.Member | OrganizationRoles.Approver,
            "ops@example.com", clock);

        membership.OrganizationId.ShouldBe(AnOrganization);
        membership.UserId.ShouldBe(AUser);
        membership.Roles.ShouldBe(OrganizationRoles.Member | OrganizationRoles.Approver);
        membership.ProvisionedBy.ShouldBe("ops@example.com");
        membership.Status.ShouldBe(MembershipStatus.Active);
        membership.CreatedAt.ShouldBe(clock.UtcNow);
        membership.RevokedAt.ShouldBeNull();
        membership.RevokedBy.ShouldBeNull();
    }

    [Fact]
    public void A_membership_without_an_organization_is_refused() =>
        Should.Throw<DomainException>(() => OrganizationMembership.Provision(
                Guid.Empty, AUser, OrganizationRoles.Member, "ops", new TestClock()))
            .Code.ShouldBe("membership.organization_required");

    [Fact]
    public void A_membership_without_a_user_is_refused() =>
        Should.Throw<DomainException>(() => OrganizationMembership.Provision(
                AnOrganization, Guid.Empty, OrganizationRoles.Member, "ops", new TestClock()))
            .Code.ShouldBe("membership.user_required");

    /// <summary>
    /// Attribution is not optional. ADR-0009 defines proofing as the operator's
    /// recorded assertion, so a membership nobody signed records a grant of
    /// access that cannot be traced to a decision.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_membership_nobody_asserted_is_refused(string? provisionedBy) =>
        Should.Throw<DomainException>(() => OrganizationMembership.Provision(
                AnOrganization, AUser, OrganizationRoles.Member, provisionedBy!, new TestClock()))
            .Code.ShouldBe("membership.provisioner_required");

    [Fact]
    public void A_membership_with_no_roles_is_refused() =>
        Should.Throw<DomainException>(() => Provision(OrganizationRoles.None))
            .Code.ShouldBe("membership.role_required");

    /// <summary>
    /// A cast integer is the realistic way an undefined role arrives — from a
    /// hand-written UPDATE, a deserializer, or a future version's value read by
    /// an older binary. <c>Enum.IsDefined</c> would be the wrong guard here: it
    /// rejects the legitimate <c>Member | Approver</c> combination.
    /// </summary>
    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(999)]
    [InlineData(-1)]
    public void A_role_this_application_does_not_define_is_refused(int roles) =>
        Should.Throw<DomainException>(() => Provision((OrganizationRoles)roles))
            .Code.ShouldBe("membership.invalid_role");

    [Fact]
    public void Both_roles_together_are_valid()
    {
        var membership = Provision(OrganizationRoles.Member | OrganizationRoles.Approver);

        membership.HasRole(OrganizationRoles.Member).ShouldBeTrue();
        membership.HasRole(OrganizationRoles.Approver).ShouldBeTrue();
        membership.CanApprove.ShouldBeTrue();
    }

    [Fact]
    public void A_member_is_not_an_approver()
    {
        var membership = Provision(OrganizationRoles.Member);

        membership.HasRole(OrganizationRoles.Member).ShouldBeTrue();
        membership.HasRole(OrganizationRoles.Approver).ShouldBeFalse();
        membership.CanApprove.ShouldBeFalse();
    }

    /// <summary>
    /// An approver who is not also a member still approves. The roles are
    /// independent bits, not a hierarchy, and nothing should quietly imply one
    /// from the other.
    /// </summary>
    [Fact]
    public void An_approver_alone_is_a_valid_membership()
    {
        var membership = Provision(OrganizationRoles.Approver);

        membership.CanApprove.ShouldBeTrue();
        membership.HasRole(OrganizationRoles.Member).ShouldBeFalse();
    }

    // -----------------------------------------------------------------------
    // Role changes
    // -----------------------------------------------------------------------

    [Fact]
    public void Roles_can_be_replaced()
    {
        var clock = new TestClock();
        var membership = Provision(OrganizationRoles.Member, clock);

        clock.Advance(TimeSpan.FromMinutes(1));
        membership.ChangeRoles(
            OrganizationRoles.Member | OrganizationRoles.Approver, "promoter@example.com", clock);

        membership.CanApprove.ShouldBeTrue();
        membership.UpdatedAt.ShouldBe(clock.UtcNow);

        // W3-002: the privileged change is attributed to whoever made it, not to
        // whoever first provisioned the account.
        membership.RolesChangedBy.ShouldBe("promoter@example.com");
        membership.RolesChangedAt.ShouldBe(clock.UtcNow);
        membership.ProvisionedBy.ShouldBe("ops@example.com");
    }

    /// <summary>
    /// The append-only chain, in order, each entry naming its own actor.
    ///
    /// <para>W3-002's core failure was that a role change left no durable trace of
    /// who made it. This asserts the whole sequence rather than the latest value,
    /// because "the most recent actor is right" is also true of an implementation
    /// that overwrites history.</para>
    /// </summary>
    [Fact]
    public void Every_change_appends_an_attributed_audit_entry()
    {
        var clock = new TestClock();

        var membership = OrganizationMembership.Provision(
            AnOrganization, AUser, OrganizationRoles.Member, "alice@ops", clock);

        clock.Advance(TimeSpan.FromHours(1));
        membership.ChangeRoles(
            OrganizationRoles.Member | OrganizationRoles.Approver, "bob@ops", clock);

        clock.Advance(TimeSpan.FromHours(1));
        membership.Revoke("carol@security", clock);

        membership.Events.Count.ShouldBe(3);

        membership.Events[0].Type.ShouldBe(MembershipEventType.Provisioned);
        membership.Events[0].Actor.ShouldBe("alice@ops");
        membership.Events[0].PreviousRoles.ShouldBeNull();
        membership.Events[0].NewRoles.ShouldBe(OrganizationRoles.Member);

        membership.Events[1].Type.ShouldBe(MembershipEventType.RolesChanged);
        membership.Events[1].Actor.ShouldBe("bob@ops");
        membership.Events[1].PreviousRoles.ShouldBe(OrganizationRoles.Member);
        membership.Events[1].NewRoles.ShouldBe(
            OrganizationRoles.Member | OrganizationRoles.Approver);

        membership.Events[2].Type.ShouldBe(MembershipEventType.Revoked);
        membership.Events[2].Actor.ShouldBe("carol@security");
        membership.Events[2].PreviousRoles.ShouldBe(
            OrganizationRoles.Member | OrganizationRoles.Approver);
        membership.Events[2].NewRoles.ShouldBeNull();

        // Each entry is self-contained: an auditor reading this table alone can
        // say who changed whose access, in which tenant.
        membership.Events.ShouldAllBe(e => e.MembershipId == membership.Id);
        membership.Events.ShouldAllBe(e => e.OrganizationId == AnOrganization);
        membership.Events.ShouldAllBe(e => e.UserId == AUser);

        // Ordered, and timestamped from the injected clock rather than wall time.
        membership.Events.Select(e => e.OccurredAt).ShouldBeInOrder();

        // History is not overwritten: the original provisioner survives the
        // promotion that W3-002 was about.
        membership.ProvisionedBy.ShouldBe("alice@ops");
        membership.RolesChangedBy.ShouldBe("bob@ops");
        membership.RevokedBy.ShouldBe("carol@security");
    }

    [Fact]
    public void An_unattributed_role_change_is_refused()
    {
        var membership = Provision(OrganizationRoles.Member);

        Should.Throw<DomainException>(() =>
                membership.ChangeRoles(OrganizationRoles.Approver, "  ", new TestClock()))
            .Code.ShouldBe("membership.role_changer_required");

        // Refused means refused: no role change, and nothing appended.
        membership.Roles.ShouldBe(OrganizationRoles.Member);
        membership.Events.Count.ShouldBe(1);
    }

    /// <summary>
    /// <c>MigrationBaseline</c> is migration-only, and the aggregate must never
    /// produce it (W3-RR-002).
    ///
    /// <para>It means "this is the state the upgrade observed", which is a claim
    /// only the migration is entitled to make. If a runtime path ever emitted
    /// one, a reader could no longer tell a reconstructed chain from an observed
    /// one — which is the entire value of having a separate type.</para>
    ///
    /// <para>Exercises every operation the aggregate has, so a new one that
    /// reached for the type would fail here.</para>
    /// </summary>
    [Fact]
    public void The_aggregate_never_produces_a_migration_baseline()
    {
        var clock = new TestClock();

        var membership = OrganizationMembership.Provision(
            AnOrganization, AUser, OrganizationRoles.Member, "ops", clock);

        membership.ChangeRoles(OrganizationRoles.Approver, "ops", clock);
        membership.Revoke("security", clock);

        membership.Events.ShouldNotBeEmpty("the operations above must have recorded something");
        membership.Events.ShouldAllBe(e => e.Type != MembershipEventType.MigrationBaseline);
    }

    [Fact]
    public void The_audit_chain_cannot_be_mutated_through_the_property()
    {
        var membership = Provision();

        MutationAttempt.TryClear(membership.Events);

        membership.Events.Count.ShouldBe(1);
    }

    /// <summary>
    /// Removing the last role is refused rather than treated as "no access". An
    /// empty membership would grant nothing while still occupying the single
    /// active slot per user, so the account would be unprovisionable until
    /// somebody noticed the empty row. <c>Revoke</c> is the operation that means
    /// no access, and it says so in the data.
    /// </summary>
    [Fact]
    public void Changing_roles_to_none_is_refused()
    {
        var membership = Provision(OrganizationRoles.Member | OrganizationRoles.Approver);

        Should.Throw<DomainException>(() =>
                membership.ChangeRoles(OrganizationRoles.None, "ops", new TestClock()))
            .Code.ShouldBe("membership.role_required");

        membership.Roles.ShouldBe(OrganizationRoles.Member | OrganizationRoles.Approver);
    }

    [Fact]
    public void Changing_roles_to_an_undefined_value_is_refused()
    {
        var membership = Provision();

        Should.Throw<DomainException>(() =>
                membership.ChangeRoles((OrganizationRoles)8, "ops", new TestClock()))
            .Code.ShouldBe("membership.invalid_role");

        membership.Roles.ShouldBe(OrganizationRoles.Member);
    }

    // -----------------------------------------------------------------------
    // Revocation
    // -----------------------------------------------------------------------

    [Fact]
    public void Revoking_records_who_and_when()
    {
        var clock = new TestClock();
        var membership = Provision(OrganizationRoles.Approver, clock);

        clock.Advance(TimeSpan.FromHours(2));
        membership.Revoke("security@example.com", clock);

        membership.Status.ShouldBe(MembershipStatus.Revoked);
        membership.RevokedBy.ShouldBe("security@example.com");
        membership.RevokedAt.ShouldBe(clock.UtcNow);
    }

    /// <summary>
    /// A revoked membership answers false to every authority question, without
    /// its roles having to be cleared. Two sources of truth for "may this person
    /// approve?" is how one of them ends up stale.
    /// </summary>
    [Fact]
    public void A_revoked_membership_grants_nothing()
    {
        var membership = Provision(OrganizationRoles.Member | OrganizationRoles.Approver);

        membership.Revoke("security", new TestClock());

        membership.IsActive.ShouldBeFalse();
        membership.CanApprove.ShouldBeFalse();
        membership.HasRole(OrganizationRoles.Member).ShouldBeFalse();
        membership.HasRole(OrganizationRoles.Approver).ShouldBeFalse();

        // The roles are still on the row, as the record of what was withdrawn.
        membership.Roles.ShouldBe(OrganizationRoles.Member | OrganizationRoles.Approver);
    }

    [Fact]
    public void A_revocation_nobody_signed_is_refused() =>
        Should.Throw<DomainException>(() => Provision().Revoke("  ", new TestClock()))
            .Code.ShouldBe("membership.revoker_required");

    [Fact]
    public void A_membership_cannot_be_revoked_twice()
    {
        var membership = Provision();
        membership.Revoke("security", new TestClock());

        Should.Throw<DomainException>(() => membership.Revoke("security", new TestClock()))
            .Code.ShouldBe("membership.already_revoked");
    }

    [Fact]
    public void A_revoked_membership_cannot_have_its_roles_changed()
    {
        var membership = Provision();
        membership.Revoke("security", new TestClock());

        Should.Throw<DomainException>(() =>
                membership.ChangeRoles(OrganizationRoles.Approver, "ops", new TestClock()))
            .Code.ShouldBe("membership.revoked");
    }

    [Fact]
    public void HasRole_is_false_for_the_empty_role_set() =>
        Provision().HasRole(OrganizationRoles.None).ShouldBeFalse();

    // -----------------------------------------------------------------------
    // Organizations
    // -----------------------------------------------------------------------

    [Fact]
    public void An_organization_trims_its_name() =>
        Organization.Create("  Acme  ", new TestClock()).Name.ShouldBe("Acme");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_organization_must_have_a_name(string name) =>
        Should.Throw<DomainException>(() => Organization.Create(name, new TestClock()))
            .Code.ShouldBe("organization.name_required");

    [Fact]
    public void An_organization_name_is_bounded() =>
        Should.Throw<DomainException>(() => Organization.Create(new string('a', 201), new TestClock()))
            .Code.ShouldBe("organization.name_too_long");

    // -----------------------------------------------------------------------
    // The Approver guard the application layer hands to callers
    // -----------------------------------------------------------------------

    /// <summary>
    /// D-3's rule: only an Approver may approve, reject or waive.
    ///
    /// <para>The guard throws rather than returning false so that a caller cannot
    /// forget to branch on the answer. W2-001 and W2-RR-002 were both a value
    /// computed and never consulted; a <c>void</c> that throws has no such
    /// failure mode.</para>
    /// </summary>
    [Fact]
    public void A_member_is_refused_approval_authority() =>
        Should.Throw<DomainException>(() =>
                new OrganizationAccess(AnOrganization, AUser, OrganizationRoles.Member)
                    .RequireApprover())
            .Code.ShouldBe("authz.approver_required");

    [Theory]
    [InlineData(OrganizationRoles.Approver)]
    [InlineData(OrganizationRoles.Member | OrganizationRoles.Approver)]
    public void An_approver_passes_the_approval_guard(OrganizationRoles roles) =>
        new OrganizationAccess(AnOrganization, AUser, roles).RequireApprover();

    [Fact]
    public void Access_reports_the_roles_it_was_resolved_with()
    {
        var access = new OrganizationAccess(AnOrganization, AUser, OrganizationRoles.Member);

        access.IsMember.ShouldBeTrue();
        access.IsApprover.ShouldBeFalse();
        access.OrganizationId.ShouldBe(AnOrganization);
        access.UserId.ShouldBe(AUser);
    }
}
