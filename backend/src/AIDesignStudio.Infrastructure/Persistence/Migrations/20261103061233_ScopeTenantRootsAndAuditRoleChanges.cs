using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIDesignStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScopeTenantRootsAndAuditRoleChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "organization_id",
                schema: "app",
                table: "screens",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "roles_changed_at",
                schema: "auth",
                table: "organization_memberships",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "roles_changed_by",
                schema: "auth",
                table: "organization_memberships",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "organization_id",
                schema: "app",
                table: "feature_inventories",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "organization_id",
                schema: "app",
                table: "design_systems",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "organization_id",
                schema: "app",
                table: "design_contracts",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Backfill the tenant key from the owning project (W3-001).
            //
            // Without this, every pre-existing row keeps the all-zero default and
            // becomes invisible to every organization — fail-closed, but also
            // data silently lost from the application's point of view. The value
            // is not a guess: each of these tables already carries the project it
            // belongs to, and a project's organization is immutable.
            //
            // Written as plain UPDATEs rather than a loop so the whole backfill is
            // part of the migration's transaction.
            migrationBuilder.Sql(
                """
                UPDATE app.design_contracts c
                   SET organization_id = p.organization_id
                  FROM app.projects p
                 WHERE p.id = c.project_id;

                UPDATE app.design_systems d
                   SET organization_id = p.organization_id
                  FROM app.projects p
                 WHERE p.id = d.project_id;

                UPDATE app.screens s
                   SET organization_id = p.organization_id
                  FROM app.projects p
                 WHERE p.id = s.project_id;

                UPDATE app.feature_inventories i
                   SET organization_id = p.organization_id
                  FROM app.projects p
                 WHERE p.id = i.project_id;
                """);

            migrationBuilder.CreateTable(
                name: "organization_membership_events",
                schema: "auth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    previous_roles = table.Column<int>(type: "integer", nullable: true),
                    new_roles = table.Column<int>(type: "integer", nullable: true),
                    actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_organization_membership_events", x => x.id);
                    table.CheckConstraint("ck_organization_membership_events_type", "type IN ('Provisioned', 'RolesChanged', 'Revoked')");
                    table.ForeignKey(
                        name: "fk_organization_membership_events_organization_memberships_mem",
                        column: x => x.membership_id,
                        principalSchema: "auth",
                        principalTable: "organization_memberships",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_screens_organization_id",
                schema: "app",
                table: "screens",
                column: "organization_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_organization_memberships_role_change_audit",
                schema: "auth",
                table: "organization_memberships",
                sql: "(roles_changed_at IS NULL) = (roles_changed_by IS NULL)");

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

            migrationBuilder.CreateIndex(
                name: "ix_organization_membership_events_membership_id_occurred_at",
                schema: "auth",
                table: "organization_membership_events",
                columns: new[] { "membership_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_organization_membership_events_organization_id_occurred_at",
                schema: "auth",
                table: "organization_membership_events",
                columns: new[] { "organization_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_organization_membership_events_user_id",
                schema: "auth",
                table: "organization_membership_events",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "organization_membership_events",
                schema: "auth");

            migrationBuilder.DropIndex(
                name: "ix_screens_organization_id",
                schema: "app",
                table: "screens");

            migrationBuilder.DropCheckConstraint(
                name: "ck_organization_memberships_role_change_audit",
                schema: "auth",
                table: "organization_memberships");

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

            migrationBuilder.DropColumn(
                name: "organization_id",
                schema: "app",
                table: "screens");

            migrationBuilder.DropColumn(
                name: "roles_changed_at",
                schema: "auth",
                table: "organization_memberships");

            migrationBuilder.DropColumn(
                name: "roles_changed_by",
                schema: "auth",
                table: "organization_memberships");

            migrationBuilder.DropColumn(
                name: "organization_id",
                schema: "app",
                table: "feature_inventories");

            migrationBuilder.DropColumn(
                name: "organization_id",
                schema: "app",
                table: "design_systems");

            migrationBuilder.DropColumn(
                name: "organization_id",
                schema: "app",
                table: "design_contracts");
        }
    }
}
