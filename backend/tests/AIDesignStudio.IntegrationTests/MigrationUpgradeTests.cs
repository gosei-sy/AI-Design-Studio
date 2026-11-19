using AIDesignStudio.Application.Organizations;
using AIDesignStudio.Domain.Organizations;
using AIDesignStudio.Infrastructure.Organizations;
using AIDesignStudio.Infrastructure.Persistence;
using AIDesignStudio.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Shouldly;
using Xunit;

namespace AIDesignStudio.IntegrationTests;

/// <summary>
/// Upgrade-path tests: start at an **older** migration, seed the rows a real
/// deployment would already hold, then migrate forward.
///
/// <para><b>Why this file exists.</b> Both re-review findings were invisible to
/// every existing test, for the same structural reason: the fixture migrates to
/// latest before any test runs, and the aggregates create internally consistent
/// data. W3-RR-001 (a tenant key disagreeing with its project) and W3-RR-002 (a
/// membership with no audit entry) are only reachable from a database that
/// already had rows when the correction arrived. A suite that can only build
/// *new* data can only verify *new* data.</para>
///
/// <para>Each test provisions and drops its own database, because it manipulates
/// schema state and must not disturb the shared one.</para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class MigrationUpgradeTests(DatabaseFixture fixture)
{
    /// <summary>The migration that added the tenant column but no constraint.</summary>
    private const string BeforeTheConstraint = "20261103061233_ScopeTenantRootsAndAuditRoleChanges";

    /// <summary>The migration before the audit-event table existed.</summary>
    private const string BeforeTheAuditChain = "20261103040427_AddOrganizationsAndMemberships";

    private static readonly Guid OrganizationA = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid OrganizationB = Guid.Parse("66666666-6666-4666-8666-666666666666");

    private void RequireDatabase() => Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AppDbContext ContextFor(string connectionString) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(connectionString, npgsql => npgsql
                    .MigrationsHistoryTable("__ef_migrations_history", "app"))
                .UseSnakeCaseNamingConvention()
                .Options,
            SystemOrganizationScope.Instance);

    /// <summary>
    /// Migrates to a named migration, or to latest when <paramref name="target"/>
    /// is null. <c>Database.MigrateAsync()</c> only goes to latest, so stopping
    /// part-way needs the migrator directly.
    /// </summary>
    private static async Task MigrateAsync(string connectionString, string? target = null)
    {
        await using var context = ContextFor(connectionString);

        await context.GetService<IMigrator>().MigrateAsync(target);
    }

    private async Task<T> QueryAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);

        await using var command = new NpgsqlCommand(sql, connection);

        var value = await command.ExecuteScalarAsync(Ct);

        value.ShouldNotBeNull($"the probe returned no value: {sql}");

        return (T)value;
    }

    private async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);

        await using var command = new NpgsqlCommand(sql, connection);

        await command.ExecuteNonQueryAsync(Ct);
    }

    // =======================================================================
    // W3-RR-001 — the mismatched pair, before and after
    // =======================================================================

    /// <summary>
    /// The re-review's own probe, made permanent: accepted before the
    /// constraint, refused after it.
    ///
    /// <para>A one-sided test would be weaker. Asserting only that the insert
    /// now fails leaves open whether it ever succeeded — and the finding was
    /// precisely that it did. Asserting both halves in one test records the
    /// defect and its fix in the same place, so neither can be read without the
    /// other.</para>
    /// </summary>
    [Fact]
    public async Task A_mismatched_tenant_and_project_is_accepted_before_the_constraint_and_refused_after()
    {
        RequireDatabase();

        var connectionString = await fixture.CreateDatabaseAsync("rr001", Ct);

        try
        {
            await MigrateAsync(connectionString, BeforeTheConstraint);
            await SeedTwoOrganizationsWithProjectsAsync(connectionString);

            // --- Before: exactly what the reviewer ran ----------------------
            await ExecuteAsync(connectionString,
                $"""
                INSERT INTO app.design_contracts (id, project_id, organization_id, created_at)
                VALUES ('{Guid.CreateVersion7()}', '{ProjectOfB}', '{OrganizationA}', now());
                """);

            (await QueryAsync<long>(connectionString,
                $"""
                SELECT count(*) FROM app.design_contracts c
                  JOIN app.projects p ON p.id = c.project_id
                 WHERE c.organization_id <> p.organization_id
                """))
                .ShouldBe(1, "the pre-constraint schema accepts the mismatch — this is the finding");

            // --- The migration resolves it from the authoritative project ----
            await MigrateAsync(connectionString);

            (await QueryAsync<long>(connectionString,
                $"""
                SELECT count(*) FROM app.design_contracts c
                  JOIN app.projects p ON p.id = c.project_id
                 WHERE c.organization_id <> p.organization_id
                """))
                .ShouldBe(0, "the migration resolves a mismatch to the project's organization");

            (await QueryAsync<Guid>(connectionString,
                $"SELECT organization_id FROM app.design_contracts LIMIT 1"))
                .ShouldBe(OrganizationB, "resolved to the project's tenant, not the row's claim");

            // --- After: the same shape of insert is now refused -------------
            //
            // Against ProjectOfBWithoutAContract, not ProjectOfB: the row above
            // already occupies ProjectOfB's one-contract-per-project slot, so
            // reusing it would hit the unique index (23505) before the foreign
            // key was ever consulted — and the test would then pass while
            // proving nothing about tenant ownership. A probe asserting one
            // constraint must violate only that constraint.
            var failure = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(
                connectionString,
                $"""
                INSERT INTO app.design_contracts (id, project_id, organization_id, created_at)
                VALUES ('{Guid.CreateVersion7()}', '{ProjectOfBWithoutAContract}',
                        '{OrganizationA}', now());
                """));

            failure.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
            failure.ConstraintName.ShouldBe("fk_design_contracts_project_tenant");
        }
        finally
        {
            await fixture.DropDatabaseAsync(connectionString);
        }
    }

    /// <summary>
    /// An orphaned project reference stops the migration, loudly, and leaves the
    /// schema untouched.
    ///
    /// <para>Resolving is right for a mismatch — the project knows the answer.
    /// An orphan has no answer: nothing can determine which tenant owns a row
    /// whose project does not exist, and deleting a tenant's data is not a
    /// migration's decision. So it fails, names the table, and rolls back.</para>
    /// </summary>
    [Fact]
    public async Task An_orphaned_project_reference_fails_the_migration_and_adds_no_constraint()
    {
        RequireDatabase();

        var connectionString = await fixture.CreateDatabaseAsync("rr001orphan", Ct);

        try
        {
            await MigrateAsync(connectionString, BeforeTheConstraint);
            await SeedTwoOrganizationsWithProjectsAsync(connectionString);

            await ExecuteAsync(connectionString,
                $"""
                INSERT INTO app.screens (id, project_id, organization_id, key, title, created_at)
                VALUES ('{Guid.CreateVersion7()}', '{Guid.CreateVersion7()}', '{OrganizationB}',
                        '/orphan', 'Orphan', now());
                """);

            var failure = await Should.ThrowAsync<PostgresException>(() =>
                MigrateAsync(connectionString));

            failure.MessageText.ShouldContain("Cannot enforce project tenant ownership");
            failure.MessageText.ShouldContain("app.screens");
            failure.MessageText.ShouldContain("will not guess");

            // The whole migration is one transaction, so nothing was applied.
            (await QueryAsync<long>(connectionString,
                "SELECT count(*) FROM pg_constraint WHERE conname = 'fk_screens_project_tenant'"))
                .ShouldBe(0, "a failed migration must leave no half-applied constraint");
        }
        finally
        {
            await fixture.DropDatabaseAsync(connectionString);
        }
    }

    private static readonly Guid ProjectOfA = Guid.Parse("aaaaaaaa-0000-4000-8000-00000000000a");
    private static readonly Guid ProjectOfB = Guid.Parse("bbbbbbbb-0000-4000-8000-00000000000b");

    /// <summary>
    /// A second project of organization B, kept free of a contract so the
    /// post-migration probe can violate the tenant foreign key and nothing else.
    /// </summary>
    private static readonly Guid ProjectOfBWithoutAContract =
        Guid.Parse("bbbbbbbb-0000-4000-8000-00000000000c");

    private Task SeedTwoOrganizationsWithProjectsAsync(string connectionString) =>
        ExecuteAsync(connectionString,
            $"""
            INSERT INTO auth.organizations (id, name, created_at)
            VALUES ('{OrganizationA}', 'Org A', now()), ('{OrganizationB}', 'Org B', now());

            INSERT INTO app.projects (id, organization_id, name, kind, created_at)
            VALUES ('{ProjectOfA}', '{OrganizationA}', 'A project', 'NewProduct', now()),
                   ('{ProjectOfB}', '{OrganizationB}', 'B project', 'NewProduct', now()),
                   ('{ProjectOfBWithoutAContract}', '{OrganizationB}', 'B spare', 'NewProduct',
                    now());
            """);

    // =======================================================================
    // W3-RR-002 — the audit baseline for memberships that predate the chain
    // =======================================================================

    /// <summary>
    /// A membership that existed before the event table gets an honest baseline,
    /// and nothing is fabricated as observed.
    ///
    /// <para>The Product Owner corrected the first design here, and the
    /// correction is what this asserts. A baseline carrying
    /// <c>provisioned_by</c> + the current roles + <c>created_at</c> would claim
    /// the person held <i>these</i> roles at <i>that</i> time — unknowable for
    /// any membership whose roles changed before the upgrade. So the baseline
    /// states only what the migration could observe: its own identity, its own
    /// run time, and the role set as found.</para>
    /// </summary>
    [Fact]
    public async Task A_membership_that_predates_the_audit_chain_gets_an_honest_baseline()
    {
        RequireDatabase();

        var connectionString = await fixture.CreateDatabaseAsync("rr002", Ct);

        try
        {
            await MigrateAsync(connectionString, BeforeTheAuditChain);

            var activeUser = Guid.CreateVersion7();
            var revokedUser = Guid.CreateVersion7();
            var activeMembership = Guid.CreateVersion7();
            var revokedMembership = Guid.CreateVersion7();

            await ExecuteAsync(connectionString,
                $"""
                INSERT INTO auth.organizations (id, name, created_at)
                VALUES ('{OrganizationA}', 'Legacy Org', now());

                INSERT INTO auth.users (id, email_confirmed, phone_number_confirmed,
                                        two_factor_enabled, lockout_enabled, access_failed_count,
                                        email, user_name)
                VALUES ('{activeUser}', true, false, false, true, 0, 'a@legacy.test', 'a@legacy.test'),
                       ('{revokedUser}', true, false, false, true, 0, 'r@legacy.test', 'r@legacy.test');

                INSERT INTO auth.organization_memberships
                    (id, organization_id, user_id, roles, status, provisioned_by, created_at)
                VALUES ('{activeMembership}', '{OrganizationA}', '{activeUser}', 3, 'Active',
                        'legacy-operator', now() - interval '90 days');

                INSERT INTO auth.organization_memberships
                    (id, organization_id, user_id, roles, status, provisioned_by, created_at,
                     revoked_at, revoked_by)
                VALUES ('{revokedMembership}', '{OrganizationA}', '{revokedUser}', 1, 'Revoked',
                        'legacy-operator', now() - interval '60 days',
                        now() - interval '30 days', 'legacy-security');
                """);

            await MigrateAsync(connectionString);

            await using var db = ContextFor(connectionString);

            var active = await db.OrganizationMemberships.Include(m => m.Events)
                .AsNoTracking().SingleAsync(m => m.Id == activeMembership, Ct);

            // --- The baseline states an observation, not a provisioning event ---
            var baseline = active.Events.Single();
            baseline.Type.ShouldBe(MembershipEventType.MigrationBaseline);
            baseline.Actor.ShouldBe(
                "system:migration/EnforceProjectTenantOwnershipAndBaselineAudit",
                "the actor names the migration, not the operator who granted access");
            baseline.PreviousRoles.ShouldBeNull("what came before the upgrade is unknowable");
            baseline.NewRoles.ShouldBe(
                OrganizationRoles.Member | OrganizationRoles.Approver,
                "the roles observed when the migration ran");
            baseline.OccurredAt.ShouldBeGreaterThan(
                active.CreatedAt.AddDays(1),
                "occurred_at is migration time, not the membership's created_at");

            // --- The original attribution is still recorded, separately ---------
            active.ProvisionedBy.ShouldBe("legacy-operator");
            active.RolesChangedBy.ShouldBeNull();

            // --- A revoked legacy row also gets its recorded revocation ---------
            var revoked = await db.OrganizationMemberships.Include(m => m.Events)
                .AsNoTracking().SingleAsync(m => m.Id == revokedMembership, Ct);

            revoked.Events.Count.ShouldBe(2);

            var reconstructed = revoked.Events.Single(e => e.Type == MembershipEventType.Revoked);
            reconstructed.Actor.ShouldBe("legacy-security", "revoked_by is recorded, so it is usable");
            reconstructed.PreviousRoles.ShouldBe(OrganizationRoles.Member);
            reconstructed.NewRoles.ShouldBeNull();
            reconstructed.OccurredAt.ShouldBe(
                revoked.RevokedAt!.Value, TimeSpan.FromSeconds(1),
                "revoked_at is recorded, so the reconstruction uses it");

            // --- Nothing claims to be an observed provisioning ------------------
            (await QueryAsync<long>(connectionString,
                "SELECT count(*) FROM auth.organization_membership_events "
                + "WHERE type = 'Provisioned'"))
                .ShouldBe(0, "no entry may be labelled Provisioned: none was observed");

            // Idempotency has its own test — Re_running_the_backfill_statements_
            // changes_nothing — because re-migrating proves nothing here
            // (W3-RR2-002).
        }
        finally
        {
            await fixture.DropDatabaseAsync(connectionString);
        }
    }

    /// <summary>
    /// A membership created *after* the upgrade has a real provisioning event and
    /// no baseline.
    ///
    /// <para>Without this, a backfill that baselined every membership — including
    /// ones the aggregate had just written a proper <c>Provisioned</c> entry for —
    /// would pass the test above. It builds its own membership rather than
    /// searching the shared database for one, because a test that skips when it
    /// finds nothing is a test that reports success for having looked.</para>
    /// </summary>
    [Fact]
    public async Task A_membership_created_after_the_upgrade_has_no_baseline()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();

        var clock = new SharedKernel.SystemClock();

        var organization = Organization.Create($"post-upgrade-{Guid.CreateVersion7():N}", clock);
        var user = new Infrastructure.Identity.ApplicationUser(
            $"post-upgrade-{Guid.CreateVersion7():N}@example.test");

        db.Organizations.Add(organization);
        db.Users.Add(user);
        await db.SaveChangesAsync(Ct);

        var membership = OrganizationMembership.Provision(
            organization.Id, user.Id, OrganizationRoles.Member, "ops@example.test", clock);

        db.OrganizationMemberships.Add(membership);
        await db.SaveChangesAsync(Ct);

        var reloaded = await db.OrganizationMemberships.Include(m => m.Events)
            .AsNoTracking().SingleAsync(m => m.Id == membership.Id, Ct);

        reloaded.Events.Select(e => e.Type).ShouldBe([MembershipEventType.Provisioned]);
        reloaded.Events.Single().Actor.ShouldBe("ops@example.test");
        reloaded.Events.ShouldNotContain(e => e.Type == MembershipEventType.MigrationBaseline);
    }

    /// <summary>
    /// A membership **in the upgrade window** keeps its observed chain and gets
    /// no baseline (W3-RR2-001).
    ///
    /// <para>The window is real: migration 5 created the event table, so a
    /// deployment running at migration 5 writes genuine <c>Provisioned</c>
    /// entries through the aggregate. Those memberships do not predate the audit
    /// chain, and the first predicate — "no <c>MigrationBaseline</c> yet" —
    /// baselined them anyway. That labels an observed chain as reconstructed and
    /// destroys the one signal distinguishing them, which is the whole point of
    /// having a separate type.</para>
    ///
    /// <para>The previous test could not see this: it created its membership
    /// after the latest migration had already run, so there was no window to be
    /// in.</para>
    /// </summary>
    [Fact]
    public async Task A_membership_with_an_observed_event_is_not_baselined_by_the_upgrade()
    {
        RequireDatabase();

        var connectionString = await fixture.CreateDatabaseAsync("rr2001", Ct);

        try
        {
            await MigrateAsync(connectionString, BeforeTheConstraint);

            var observedUser = Guid.CreateVersion7();
            var observedMembership = Guid.CreateVersion7();
            var legacyUser = Guid.CreateVersion7();
            var legacyMembership = Guid.CreateVersion7();
            var revokedInWindowUser = Guid.CreateVersion7();
            var revokedInWindowMembership = Guid.CreateVersion7();

            await ExecuteAsync(connectionString,
                $"""
                INSERT INTO auth.organizations (id, name, created_at)
                VALUES ('{OrganizationA}', 'Window Org', now());

                INSERT INTO auth.users (id, email_confirmed, phone_number_confirmed,
                                        two_factor_enabled, lockout_enabled, access_failed_count,
                                        email, user_name)
                VALUES ('{observedUser}', true, false, false, true, 0, 'o@w.test', 'o@w.test'),
                       ('{legacyUser}', true, false, false, true, 0, 'l@w.test', 'l@w.test'),
                       ('{revokedInWindowUser}', true, false, false, true, 0, 'r@w.test', 'r@w.test');

                -- In the window: provisioned through the audited path.
                INSERT INTO auth.organization_memberships
                    (id, organization_id, user_id, roles, status, provisioned_by, created_at)
                VALUES ('{observedMembership}', '{OrganizationA}', '{observedUser}', 1, 'Active',
                        'observed-operator', now());

                INSERT INTO auth.organization_membership_events
                    (id, membership_id, organization_id, user_id, type, previous_roles, new_roles,
                     actor, occurred_at)
                VALUES (gen_random_uuid(), '{observedMembership}', '{OrganizationA}',
                        '{observedUser}', 'Provisioned', NULL, 1, 'observed-operator', now());

                -- Also in the window, and revoked through the audited path.
                INSERT INTO auth.organization_memberships
                    (id, organization_id, user_id, roles, status, provisioned_by, created_at,
                     revoked_at, revoked_by)
                VALUES ('{revokedInWindowMembership}', '{OrganizationA}', '{revokedInWindowUser}',
                        1, 'Revoked', 'observed-operator', now() - interval '2 days',
                        now() - interval '1 day', 'observed-security');

                INSERT INTO auth.organization_membership_events
                    (id, membership_id, organization_id, user_id, type, previous_roles, new_roles,
                     actor, occurred_at)
                VALUES (gen_random_uuid(), '{revokedInWindowMembership}', '{OrganizationA}',
                        '{revokedInWindowUser}', 'Provisioned', NULL, 1, 'observed-operator',
                        now() - interval '2 days'),
                       (gen_random_uuid(), '{revokedInWindowMembership}', '{OrganizationA}',
                        '{revokedInWindowUser}', 'Revoked', 1, NULL, 'observed-security',
                        now() - interval '1 day');

                -- Genuinely pre-chain: no events at all.
                INSERT INTO auth.organization_memberships
                    (id, organization_id, user_id, roles, status, provisioned_by, created_at)
                VALUES ('{legacyMembership}', '{OrganizationA}', '{legacyUser}', 3, 'Active',
                        'legacy-operator', now() - interval '90 days');
                """);

            await MigrateAsync(connectionString);

            // The observed membership is untouched.
            (await QueryAsync<long>(connectionString,
                $"""
                SELECT count(*) FROM auth.organization_membership_events
                 WHERE membership_id = '{observedMembership}'
                   AND type = 'MigrationBaseline'
                """))
                .ShouldBe(0, "a membership with an observed event does not predate the chain");

            (await QueryAsync<long>(connectionString,
                $"SELECT count(*) FROM auth.organization_membership_events "
                + $"WHERE membership_id = '{observedMembership}'"))
                .ShouldBe(1, "its single observed entry is left exactly as it was");

            // The one revoked inside the window gets neither a baseline nor a
            // duplicate revocation.
            (await QueryAsync<long>(connectionString,
                $"""
                SELECT count(*) FROM auth.organization_membership_events
                 WHERE membership_id = '{revokedInWindowMembership}'
                   AND type = 'MigrationBaseline'
                """))
                .ShouldBe(0);

            (await QueryAsync<long>(connectionString,
                $"""
                SELECT count(*) FROM auth.organization_membership_events
                 WHERE membership_id = '{revokedInWindowMembership}'
                   AND type = 'Revoked'
                """))
                .ShouldBe(1, "its revocation was already observed; nothing is reconstructed");

            // And the genuinely pre-chain membership still gets its baseline, so
            // the narrower predicate has not disabled the feature.
            (await QueryAsync<long>(connectionString,
                $"""
                SELECT count(*) FROM auth.organization_membership_events
                 WHERE membership_id = '{legacyMembership}'
                   AND type = 'MigrationBaseline'
                """))
                .ShouldBe(1, "a membership with no events does predate the chain");
        }
        finally
        {
            await fixture.DropDatabaseAsync(connectionString);
        }
    }

    /// <summary>
    /// The backfill statements are idempotent — proven by running **those
    /// statements** a second time (W3-RR2-002).
    ///
    /// <para>The previous assertion re-migrated an already-current database,
    /// which executes no SQL at all: it proved that EF does not re-apply a
    /// recorded migration, not that either <c>INSERT</c> is safe to repeat. This
    /// executes <c>MembershipAuditBaselineSql.Backfill</c> — the same constant
    /// the migration runs — and asserts counts **per membership and per type**,
    /// because a global total can hide one row appearing while another
    /// disappears.</para>
    /// </summary>
    [Fact]
    public async Task Re_running_the_backfill_statements_changes_nothing()
    {
        RequireDatabase();

        var connectionString = await fixture.CreateDatabaseAsync("rr2002", Ct);

        try
        {
            await MigrateAsync(connectionString, BeforeTheAuditChain);

            var activeUser = Guid.CreateVersion7();
            var revokedUser = Guid.CreateVersion7();
            var activeMembership = Guid.CreateVersion7();
            var revokedMembership = Guid.CreateVersion7();

            await ExecuteAsync(connectionString,
                $"""
                INSERT INTO auth.organizations (id, name, created_at)
                VALUES ('{OrganizationA}', 'Legacy Org', now());

                INSERT INTO auth.users (id, email_confirmed, phone_number_confirmed,
                                        two_factor_enabled, lockout_enabled, access_failed_count,
                                        email, user_name)
                VALUES ('{activeUser}', true, false, false, true, 0, 'a@l.test', 'a@l.test'),
                       ('{revokedUser}', true, false, false, true, 0, 'r@l.test', 'r@l.test');

                INSERT INTO auth.organization_memberships
                    (id, organization_id, user_id, roles, status, provisioned_by, created_at)
                VALUES ('{activeMembership}', '{OrganizationA}', '{activeUser}', 3, 'Active',
                        'legacy-operator', now() - interval '90 days');

                INSERT INTO auth.organization_memberships
                    (id, organization_id, user_id, roles, status, provisioned_by, created_at,
                     revoked_at, revoked_by)
                VALUES ('{revokedMembership}', '{OrganizationA}', '{revokedUser}', 1, 'Revoked',
                        'legacy-operator', now() - interval '60 days',
                        now() - interval '30 days', 'legacy-security');
                """);

            await MigrateAsync(connectionString);

            var fingerprint = async () => await QueryAsync<string>(connectionString,
                """
                SELECT coalesce(string_agg(membership_id || ':' || type || ':' || n, ' | '
                                           ORDER BY membership_id, type), '(empty)')
                  FROM (SELECT membership_id, type, count(*)::text AS n
                          FROM auth.organization_membership_events
                         GROUP BY membership_id, type) counted
                """);

            var before = await fingerprint();

            before.ShouldContain(":MigrationBaseline:1", Case.Sensitive);
            before.ShouldContain(":Revoked:1", Case.Sensitive);

            // The real statements, run a second time.
            await ExecuteAsync(connectionString, MembershipAuditBaselineSql.Backfill);

            (await fingerprint()).ShouldBe(
                before,
                "the backfill statements must be safe to repeat, per membership and per type");

            // And a third time, because "idempotent" is not "runs twice".
            await ExecuteAsync(connectionString, MembershipAuditBaselineSql.Backfill);

            (await fingerprint()).ShouldBe(before);
        }
        finally
        {
            await fixture.DropDatabaseAsync(connectionString);
        }
    }

    /// <summary>
    /// The migration can be rolled back with baselined memberships present
    /// (W3-RR2-003).
    ///
    /// <para><c>Down()</c> used to restore the old event-type constraint while
    /// `MigrationBaseline` rows still existed, so every downgrade failed with
    /// PostgreSQL `23514` — deterministically, for any database holding one
    /// membership. An emergency rollback broke on data the migration itself had
    /// written.</para>
    ///
    /// <para>What a rollback restores is the schema and this migration's audit
    /// rows. It deliberately does **not** restore a tenant key that `Up()`
    /// repaired: that value was wrong, and putting it back is the one thing a
    /// rollback should refuse.</para>
    /// </summary>
    [Fact]
    public async Task The_migration_rolls_back_with_baselined_memberships_present()
    {
        RequireDatabase();

        var connectionString = await fixture.CreateDatabaseAsync("rr2003", Ct);

        try
        {
            await MigrateAsync(connectionString, BeforeTheAuditChain);

            var user = Guid.CreateVersion7();
            var revokedUser = Guid.CreateVersion7();
            var observedUser = Guid.CreateVersion7();
            var observedMembership = Guid.CreateVersion7();

            await ExecuteAsync(connectionString,
                $"""
                INSERT INTO auth.organizations (id, name, created_at)
                VALUES ('{OrganizationA}', 'Rollback Org', now());

                INSERT INTO auth.users (id, email_confirmed, phone_number_confirmed,
                                        two_factor_enabled, lockout_enabled, access_failed_count,
                                        email, user_name)
                VALUES ('{user}', true, false, false, true, 0, 'a@rb.test', 'a@rb.test'),
                       ('{revokedUser}', true, false, false, true, 0, 'r@rb.test', 'r@rb.test'),
                       ('{observedUser}', true, false, false, true, 0, 'o@rb.test', 'o@rb.test');

                INSERT INTO auth.organization_memberships
                    (id, organization_id, user_id, roles, status, provisioned_by, created_at)
                VALUES ('{Guid.CreateVersion7()}', '{OrganizationA}', '{user}', 3, 'Active',
                        'legacy-operator', now() - interval '90 days');

                INSERT INTO auth.organization_memberships
                    (id, organization_id, user_id, roles, status, provisioned_by, created_at,
                     revoked_at, revoked_by)
                VALUES ('{Guid.CreateVersion7()}', '{OrganizationA}', '{revokedUser}', 1, 'Revoked',
                        'legacy-operator', now() - interval '60 days',
                        now() - interval '30 days', 'legacy-security');
                """);

            // Forward to migration 5, which creates the event table. Only then
            // can an **observed** membership exist — and it has to, so the
            // rollback is forced to remove exactly what the migration inserted
            // rather than simply emptying the table. This is the real sequence a
            // deployment goes through.
            await MigrateAsync(connectionString, BeforeTheConstraint);

            await ExecuteAsync(connectionString,
                $"""
                INSERT INTO auth.organization_memberships
                    (id, organization_id, user_id, roles, status, provisioned_by, created_at)
                VALUES ('{observedMembership}', '{OrganizationA}', '{observedUser}', 1, 'Active',
                        'observed-operator', now());

                INSERT INTO auth.organization_membership_events
                    (id, membership_id, organization_id, user_id, type, previous_roles, new_roles,
                     actor, occurred_at)
                VALUES (gen_random_uuid(), '{observedMembership}', '{OrganizationA}',
                        '{observedUser}', 'Provisioned', NULL, 1, 'observed-operator', now());
                """);

            await MigrateAsync(connectionString);

            (await QueryAsync<long>(connectionString,
                "SELECT count(*) FROM auth.organization_membership_events "
                + "WHERE type = 'MigrationBaseline'"))
                .ShouldBe(2, "both legacy memberships are baselined, so the rollback has work to do");

            // The downgrade that used to fail with 23514.
            await MigrateAsync(connectionString, BeforeTheConstraint);

            (await QueryAsync<long>(connectionString,
                "SELECT count(*) FROM auth.organization_membership_events "
                + "WHERE type = 'MigrationBaseline'"))
                .ShouldBe(0, "every baseline this migration wrote is removed");

            // The observed entry survives. "Remove exactly what Up() inserted"
            // is a stronger and more useful claim than "empty the table", and
            // only a rollback that distinguishes them is safe to run on a
            // database that has been serving traffic.
            (await QueryAsync<long>(connectionString,
                $"SELECT count(*) FROM auth.organization_membership_events "
                + $"WHERE membership_id = '{observedMembership}' AND type = 'Provisioned'"))
                .ShouldBe(1, "an observed event predates this migration and must survive it");

            (await QueryAsync<long>(connectionString,
                "SELECT count(*) FROM auth.organization_membership_events"))
                .ShouldBe(1, "the observed entry, and nothing else");

            (await QueryAsync<long>(connectionString,
                "SELECT count(*) FROM pg_constraint WHERE conname LIKE '%_project_tenant'"))
                .ShouldBe(0, "the composite tenant keys are gone with the migration");

            // The old constraint is back and rejects the type it does not know.
            var failure = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(
                connectionString,
                $"""
                INSERT INTO auth.organization_membership_events
                    (id, membership_id, organization_id, user_id, type, new_roles, actor,
                     occurred_at)
                SELECT gen_random_uuid(), m.id, m.organization_id, m.user_id,
                       'MigrationBaseline', m.roles, 'probe', now()
                  FROM auth.organization_memberships m LIMIT 1;
                """));

            failure.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            failure.ConstraintName.ShouldBe("ck_organization_membership_events_type");

            // And forward again, so the rollback is not a one-way door.
            await MigrateAsync(connectionString);

            (await QueryAsync<long>(connectionString,
                "SELECT count(*) FROM auth.organization_membership_events "
                + "WHERE type = 'MigrationBaseline'"))
                .ShouldBe(2, "re-applying the migration baselines them again");
        }
        finally
        {
            await fixture.DropDatabaseAsync(connectionString);
        }
    }
}
