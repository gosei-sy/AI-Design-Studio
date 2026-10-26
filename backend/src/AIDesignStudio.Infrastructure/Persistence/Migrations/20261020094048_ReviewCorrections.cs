using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIDesignStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReviewCorrections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_jobs_kind_idempotency_key",
                schema: "app",
                table: "jobs");

            migrationBuilder.DropIndex(
                name: "ix_design_systems_project_id",
                schema: "app",
                table: "design_systems");

            migrationBuilder.DropIndex(
                name: "ix_design_contracts_project_id",
                schema: "app",
                table: "design_contracts");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "rejected_at",
                schema: "app",
                table: "screen_versions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "rejected_by",
                schema: "app",
                table: "screen_versions",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "rejection_reason",
                schema: "app",
                table: "screen_versions",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tokens_hash",
                schema: "app",
                table: "design_system_versions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_projects_kind",
                schema: "app",
                table: "projects",
                sql: "kind IN ('NewProduct', 'Redesign')");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_organization_id",
                schema: "app",
                table: "jobs",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_organization_id_kind_idempotency_key",
                schema: "app",
                table: "jobs",
                columns: new[] { "organization_id", "kind", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_design_systems_project_id",
                schema: "app",
                table: "design_systems",
                column: "project_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_design_contracts_project_id",
                schema: "app",
                table: "design_contracts",
                column: "project_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_projects_kind",
                schema: "app",
                table: "projects");

            migrationBuilder.DropIndex(
                name: "ix_jobs_organization_id",
                schema: "app",
                table: "jobs");

            migrationBuilder.DropIndex(
                name: "ix_jobs_organization_id_kind_idempotency_key",
                schema: "app",
                table: "jobs");

            migrationBuilder.DropIndex(
                name: "ix_design_systems_project_id",
                schema: "app",
                table: "design_systems");

            migrationBuilder.DropIndex(
                name: "ix_design_contracts_project_id",
                schema: "app",
                table: "design_contracts");

            migrationBuilder.DropColumn(
                name: "rejected_at",
                schema: "app",
                table: "screen_versions");

            migrationBuilder.DropColumn(
                name: "rejected_by",
                schema: "app",
                table: "screen_versions");

            migrationBuilder.DropColumn(
                name: "rejection_reason",
                schema: "app",
                table: "screen_versions");

            migrationBuilder.DropColumn(
                name: "tokens_hash",
                schema: "app",
                table: "design_system_versions");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_kind_idempotency_key",
                schema: "app",
                table: "jobs",
                columns: new[] { "kind", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_design_systems_project_id",
                schema: "app",
                table: "design_systems",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_design_contracts_project_id",
                schema: "app",
                table: "design_contracts",
                column: "project_id");
        }
    }
}
