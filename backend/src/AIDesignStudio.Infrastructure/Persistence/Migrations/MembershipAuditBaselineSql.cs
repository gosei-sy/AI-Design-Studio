namespace AIDesignStudio.Infrastructure.Persistence.Migrations;

/// <summary>
/// The membership audit baseline statements, shared between the migration that
/// runs them and the test that proves they are idempotent (W3-RR2-002).
///
/// <para><b>Why they live here rather than inline.</b> The first version embedded
/// the SQL in the migration and asserted idempotency by migrating an
/// already-current database to current again — which executes nothing, because
/// EF skips an applied migration. The assertion therefore proved only that EF
/// does not re-run migrations, and the re-review was right to call it a no-op.
/// Idempotency is a property of **these statements**, so a test has to run
/// exactly these statements twice.</para>
///
/// <para><b>⚠ Never edit this SQL.</b> It is referenced by
/// <c>20261105091231_EnforceProjectTenantOwnershipAndBaselineAudit</c>, and an
/// applied migration must keep meaning what it meant when it was applied.
/// Changing a string here would silently change what that migration does on
/// every future fresh database while every already-upgraded database kept the
/// old behaviour. If the backfill ever needs to differ, write a new migration
/// with new SQL and leave this alone.</para>
/// </summary>
// Public rather than internal so the idempotency test can run exactly these
// statements. The alternative was InternalsVisibleTo, which hides from a reader
// of this file that a test depends on it — and this constant's whole reason for
// existing is that dependency.
public static class MembershipAuditBaselineSql
{
    /// <summary>
    /// Writes the baseline for memberships that predate the audit chain, and
    /// reconstructs the revocation for those of them that were already revoked.
    ///
    /// <para><b>The baseline predicate is "no audit events at all"</b>, not "no
    /// baseline yet". W3-RR2-001: the first version excluded only an existing
    /// `MigrationBaseline`, so a membership in the real upgrade window — created
    /// after the event table existed, carrying a genuine observed `Provisioned`
    /// entry — was given a baseline as well. That misclassifies an observed chain
    /// as a reconstructed one and destroys the only signal distinguishing them.
    /// A membership that has any event does not predate the chain, by
    /// definition.</para>
    ///
    /// <para>The reconstruction is guarded per event type <b>and</b> conditioned
    /// on a baseline existing, so it reaches only genuinely pre-chain rows: a
    /// membership revoked through the aggregate already has its own `Revoked`
    /// entry and never gets a baseline, so it is excluded twice over.</para>
    ///
    /// <para>Both statements are therefore safe to run repeatedly — asserted by
    /// <c>MigrationUpgradeTests.Re_running_the_backfill_statements_changes_nothing</c>.</para>
    /// </summary>
    public const string Backfill =
        """
        INSERT INTO auth.organization_membership_events
            (id, membership_id, organization_id, user_id, type,
             previous_roles, new_roles, actor, occurred_at)
        SELECT
            gen_random_uuid(), m.id, m.organization_id, m.user_id,
            'MigrationBaseline',
            NULL,
            m.roles,
            'system:migration/EnforceProjectTenantOwnershipAndBaselineAudit',
            now()
          FROM auth.organization_memberships m
         WHERE NOT EXISTS (
                   SELECT 1 FROM auth.organization_membership_events e
                    WHERE e.membership_id = m.id);

        INSERT INTO auth.organization_membership_events
            (id, membership_id, organization_id, user_id, type,
             previous_roles, new_roles, actor, occurred_at)
        SELECT
            gen_random_uuid(), m.id, m.organization_id, m.user_id,
            'Revoked',
            m.roles,
            NULL,
            m.revoked_by,
            m.revoked_at
          FROM auth.organization_memberships m
         WHERE m.status = 'Revoked'
           AND m.revoked_by IS NOT NULL
           AND m.revoked_at IS NOT NULL
           AND EXISTS (
                   SELECT 1 FROM auth.organization_membership_events b
                    WHERE b.membership_id = m.id
                      AND b.type = 'MigrationBaseline')
           AND NOT EXISTS (
                   SELECT 1 FROM auth.organization_membership_events e
                    WHERE e.membership_id = m.id
                      AND e.type = 'Revoked');
        """;

    /// <summary>
    /// Removes exactly what <see cref="Backfill"/> inserted, so the old
    /// event-type check constraint can be restored (W3-RR2-003).
    ///
    /// <para>Without this, <c>Down()</c> recreated a constraint allowing only
    /// `Provisioned`, `RolesChanged` and `Revoked` while `MigrationBaseline` rows
    /// were still present — PostgreSQL `23514`, deterministic for every database
    /// holding at least one membership. An emergency rollback failed on data the
    /// migration itself had created.</para>
    ///
    /// <para><b>Order matters.</b> A reconstructed revocation is identifiable only
    /// by its membership also having a baseline, so it must be deleted while the
    /// baselines still exist.</para>
    ///
    /// <para><b>What rolling back does not undo:</b> the tenant keys this
    /// migration repaired. Those rows were wrong, the project is the
    /// authoritative owner, and restoring the incorrect value would be the one
    /// irreversible thing worth refusing. A downgrade returns the schema and this
    /// migration's audit rows, not the pre-repair data.</para>
    /// </summary>
    public const string RollbackBaseline =
        """
        DELETE FROM auth.organization_membership_events e
         WHERE e.type = 'Revoked'
           AND EXISTS (
                   SELECT 1 FROM auth.organization_membership_events b
                    WHERE b.membership_id = e.membership_id
                      AND b.type = 'MigrationBaseline');

        DELETE FROM auth.organization_membership_events
         WHERE type = 'MigrationBaseline';
        """;
}
