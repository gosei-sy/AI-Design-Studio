using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIDesignStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforceProjectTenantOwnershipAndBaselineAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ---------------------------------------------------------------
            // Before any constraint is added: make the data satisfiable, or
            // stop (W3-RR-001, required correction 3).
            //
            // Two different problems, two different answers:
            //
            //   * MISMATCHED — the child's organization_id disagrees with its
            //     project's. Resolved, not rejected: the project is the
            //     authoritative owner, so the correct value is knowable. The
            //     counts are raised as notices so the operator sees what moved.
            //
            //   * ORPHANED — the child's project_id references no project.
            //     Nothing can determine the right tenant, and silently deleting
            //     a tenant's row is not a migration's decision to make. The
            //     migration fails, naming the table and the count.
            //
            // Both run inside the migration's transaction, so a failure leaves
            // the schema untouched.
            // ---------------------------------------------------------------
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    mismatched bigint;
                    orphaned bigint;
                    orphan_report text := '';
                    child record;
                BEGIN
                    FOR child IN
                        SELECT unnest(ARRAY[
                            'design_contracts', 'design_systems', 'screens',
                            'feature_inventories', 'assets'
                        ]) AS table_name
                    LOOP
                        EXECUTE format(
                            'SELECT count(*) FROM app.%I c
                               JOIN app.projects p ON p.id = c.project_id
                              WHERE c.organization_id <> p.organization_id',
                            child.table_name)
                        INTO mismatched;

                        IF mismatched > 0 THEN
                            EXECUTE format(
                                'UPDATE app.%I c
                                    SET organization_id = p.organization_id
                                   FROM app.projects p
                                  WHERE p.id = c.project_id
                                    AND c.organization_id <> p.organization_id',
                                child.table_name);

                            RAISE NOTICE
                                'app.% : % row(s) had a tenant key disagreeing with their '
                                'project and were resolved to the project''s organization.',
                                child.table_name, mismatched;
                        END IF;

                        EXECUTE format(
                            'SELECT count(*) FROM app.%I c
                              WHERE NOT EXISTS (
                                  SELECT 1 FROM app.projects p WHERE p.id = c.project_id)',
                            child.table_name)
                        INTO orphaned;

                        IF orphaned > 0 THEN
                            orphan_report := orphan_report
                                || format('app.%s: %s row(s); ', child.table_name, orphaned);
                        END IF;
                    END LOOP;

                    IF orphan_report <> '' THEN
                        RAISE EXCEPTION
                            'Cannot enforce project tenant ownership: % These rows reference a '
                            'project that does not exist, so their owning organization is not '
                            'knowable. Resolve them deliberately — the migration will not guess, '
                            'and will not delete tenant data.',
                            orphan_report;
                    END IF;
                END $$;
                """);

            migrationBuilder.DropIndex(
                name: "ix_screens_organization_id",
                schema: "app",
                table: "screens");

            migrationBuilder.DropIndex(
                name: "ix_projects_organization_id",
                schema: "app",
                table: "projects");

            migrationBuilder.DropCheckConstraint(
                name: "ck_organization_membership_events_type",
                schema: "auth",
                table: "organization_membership_events");

            migrationBuilder.DropIndex(
                name: "ix_feature_inventories_organization_id",
                schema: "app",
                table: "feature_inventories");

            migrationBuilder.DropIndex(
                name: "ix_design_systems_organization_id",
                schema: "app",
                table: "design_systems");

            migrationBuilder.DropIndex(
                name: "ix_design_contracts_organization_id",
                schema: "app",
                table: "design_contracts");

            migrationBuilder.AddUniqueConstraint(
                name: "ak_projects_organization_id_id",
                schema: "app",
                table: "projects",
                columns: new[] { "organization_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_screens_organization_id_project_id",
                schema: "app",
                table: "screens",
                columns: new[] { "organization_id", "project_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_organization_membership_events_type",
                schema: "auth",
                table: "organization_membership_events",
                sql: "type IN ('Provisioned', 'RolesChanged', 'Revoked', 'MigrationBaseline')");

            migrationBuilder.CreateIndex(
                name: "ix_feature_inventories_organization_id_project_id",
                schema: "app",
                table: "feature_inventories",
                columns: new[] { "organization_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "ix_design_systems_organization_id_project_id",
                schema: "app",
                table: "design_systems",
                columns: new[] { "organization_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "ix_design_contracts_organization_id_project_id",
                schema: "app",
                table: "design_contracts",
                columns: new[] { "organization_id", "project_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_assets_project_tenant",
                schema: "app",
                table: "assets",
                columns: new[] { "organization_id", "project_id" },
                principalSchema: "app",
                principalTable: "projects",
                principalColumns: new[] { "organization_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_design_contracts_project_tenant",
                schema: "app",
                table: "design_contracts",
                columns: new[] { "organization_id", "project_id" },
                principalSchema: "app",
                principalTable: "projects",
                principalColumns: new[] { "organization_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_design_systems_project_tenant",
                schema: "app",
                table: "design_systems",
                columns: new[] { "organization_id", "project_id" },
                principalSchema: "app",
                principalTable: "projects",
                principalColumns: new[] { "organization_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_feature_inventories_project_tenant",
                schema: "app",
                table: "feature_inventories",
                columns: new[] { "organization_id", "project_id" },
                principalSchema: "app",
                principalTable: "projects",
                principalColumns: new[] { "organization_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_screens_project_tenant",
                schema: "app",
                table: "screens",
                columns: new[] { "organization_id", "project_id" },
                principalSchema: "app",
                principalTable: "projects",
                principalColumns: new[] { "organization_id", "id" },
                onDelete: ReferentialAction.Restrict);

            // ---------------------------------------------------------------
            // An honest audit baseline for memberships that predate the event
            // chain (W3-RR-002).
            //
            // MigrationBaseline states what was *observed when this migration
            // ran*. It is deliberately NOT a reconstructed provisioning event:
            //
            //   * actor      — names this migration, not provisioned_by;
            //   * occurred_at— migration time, not the membership's created_at;
            //   * previous_roles — NULL, because what came before is unknowable;
            //   * new_roles  — the role set observed now.
            //
            // The original attribution stays where it already is, on the
            // membership row in provisioned_by / created_at. Putting those two
            // together with the current role set into one entry would assert
            // that the person held *these* roles at *that* time, which is false
            // for any membership whose roles changed before the upgrade.
            //
            // The statements live in MembershipAuditBaselineSql so the
            // idempotency test can execute exactly these statements twice
            // (W3-RR2-002) — migrating an already-current database proves
            // nothing, because EF skips an applied migration.
            // ---------------------------------------------------------------
            migrationBuilder.Sql(MembershipAuditBaselineSql.Backfill);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ---------------------------------------------------------------
            // Remove the audit rows this migration created, BEFORE the old
            // event-type constraint comes back (W3-RR2-003).
            //
            // The first version restored a constraint allowing only
            // Provisioned / RolesChanged / Revoked while MigrationBaseline rows
            // were still present, so every downgrade failed with PostgreSQL
            // 23514 — deterministically, for any database holding a single
            // membership. An emergency rollback broke on data this migration
            // had written itself.
            //
            // The tenant keys repaired in Up() are deliberately NOT restored:
            // those values were wrong, the project is the authoritative owner,
            // and putting the incorrect value back is the one thing a rollback
            // should refuse to do. A downgrade returns the schema and this
            // migration's audit rows, not the pre-repair data.
            // ---------------------------------------------------------------
            migrationBuilder.Sql(MembershipAuditBaselineSql.RollbackBaseline);

            migrationBuilder.DropForeignKey(
                name: "fk_assets_project_tenant",
                schema: "app",
                table: "assets");

            migrationBuilder.DropForeignKey(
                name: "fk_design_contracts_project_tenant",
                schema: "app",
                table: "design_contracts");

            migrationBuilder.DropForeignKey(
                name: "fk_design_systems_project_tenant",
                schema: "app",
                table: "design_systems");

            migrationBuilder.DropForeignKey(
                name: "fk_feature_inventories_project_tenant",
                schema: "app",
                table: "feature_inventories");

            migrationBuilder.DropForeignKey(
                name: "fk_screens_project_tenant",
                schema: "app",
                table: "screens");

            migrationBuilder.DropIndex(
                name: "ix_screens_organization_id_project_id",
                schema: "app",
                table: "screens");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_projects_organization_id_id",
                schema: "app",
                table: "projects");

            migrationBuilder.DropCheckConstraint(
                name: "ck_organization_membership_events_type",
                schema: "auth",
                table: "organization_membership_events");

            migrationBuilder.DropIndex(
                name: "ix_feature_inventories_organization_id_project_id",
                schema: "app",
                table: "feature_inventories");

            migrationBuilder.DropIndex(
                name: "ix_design_systems_organization_id_project_id",
                schema: "app",
                table: "design_systems");

            migrationBuilder.DropIndex(
                name: "ix_design_contracts_organization_id_project_id",
                schema: "app",
                table: "design_contracts");

            migrationBuilder.CreateIndex(
                name: "ix_screens_organization_id",
                schema: "app",
                table: "screens",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_projects_organization_id",
                schema: "app",
                table: "projects",
                column: "organization_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_organization_membership_events_type",
                schema: "auth",
                table: "organization_membership_events",
                sql: "type IN ('Provisioned', 'RolesChanged', 'Revoked')");

            migrationBuilder.CreateIndex(
                name: "ix_feature_inventories_organization_id",
                schema: "app",
                table: "feature_inventories",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_design_systems_organization_id",
                schema: "app",
                table: "design_systems",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_design_contracts_organization_id",
                schema: "app",
                table: "design_contracts",
                column: "organization_id");
        }
    }
}
