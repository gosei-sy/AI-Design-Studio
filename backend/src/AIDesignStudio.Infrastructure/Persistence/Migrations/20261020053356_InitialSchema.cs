using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIDesignStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "app");

            migrationBuilder.CreateTable(
                name: "assets",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    storage_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    content_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    original_file_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "design_contracts",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_design_contracts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "design_systems",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_design_systems", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "feature_inventories",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_feature_inventories", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "jobs",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    max_attempts = table.Column<int>(type: "integer", nullable: false),
                    run_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lease_owner = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "projects",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_projects", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "screens",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_screens", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "design_contract_versions",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    approved_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_design_contract_versions", x => x.id);
                    table.ForeignKey(
                        name: "fk_design_contract_versions_design_contracts_contract_id",
                        column: x => x.contract_id,
                        principalSchema: "app",
                        principalTable: "design_contracts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "design_system_versions",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    design_system_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    tokens_json = table.Column<string>(type: "jsonb", nullable: false),
                    approved_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_design_system_versions", x => x.id);
                    table.ForeignKey(
                        name: "fk_design_system_versions_design_systems_design_system_id",
                        column: x => x.design_system_id,
                        principalSchema: "app",
                        principalTable: "design_systems",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "feature_inventory_items",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    inventory_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    coverage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    covering_screen_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    waiver_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    waived_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_feature_inventory_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_feature_inventory_items_feature_inventories_inventory_id",
                        column: x => x.inventory_id,
                        principalSchema: "app",
                        principalTable: "feature_inventories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "screen_versions",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    screen_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    origin = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    review_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    contract_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    provider_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    provider_artifact_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    approved_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_screen_versions", x => x.id);
                    table.ForeignKey(
                        name: "fk_screen_versions_screens_screen_id",
                        column: x => x.screen_id,
                        principalSchema: "app",
                        principalTable: "screens",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_assets_organization_id_project_id",
                schema: "app",
                table: "assets",
                columns: new[] { "organization_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "ix_assets_storage_key",
                schema: "app",
                table: "assets",
                column: "storage_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_design_contract_versions_contract_id_version_number",
                schema: "app",
                table: "design_contract_versions",
                columns: new[] { "contract_id", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_contract_single_approved",
                schema: "app",
                table: "design_contract_versions",
                column: "contract_id",
                unique: true,
                filter: "status = 'Approved'");

            migrationBuilder.CreateIndex(
                name: "ix_design_contracts_project_id",
                schema: "app",
                table: "design_contracts",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_design_system_versions_design_system_id_version_number",
                schema: "app",
                table: "design_system_versions",
                columns: new[] { "design_system_id", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_design_system_single_approved",
                schema: "app",
                table: "design_system_versions",
                column: "design_system_id",
                unique: true,
                filter: "status = 'Approved'");

            migrationBuilder.CreateIndex(
                name: "ix_design_systems_project_id",
                schema: "app",
                table: "design_systems",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_feature_inventories_project_id",
                schema: "app",
                table: "feature_inventories",
                column: "project_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_feature_inventory_items_inventory_id_key",
                schema: "app",
                table: "feature_inventory_items",
                columns: new[] { "inventory_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_jobs_kind_idempotency_key",
                schema: "app",
                table: "jobs",
                columns: new[] { "kind", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_jobs_state_run_after",
                schema: "app",
                table: "jobs",
                columns: new[] { "state", "run_after" });

            migrationBuilder.CreateIndex(
                name: "ix_projects_organization_id",
                schema: "app",
                table: "projects",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_screen_versions_screen_id_version_number",
                schema: "app",
                table: "screen_versions",
                columns: new[] { "screen_id", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_screen_single_approved",
                schema: "app",
                table: "screen_versions",
                column: "screen_id",
                unique: true,
                filter: "review_state = 'Approved'");

            migrationBuilder.CreateIndex(
                name: "ix_screens_project_id_key",
                schema: "app",
                table: "screens",
                columns: new[] { "project_id", "key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "assets",
                schema: "app");

            migrationBuilder.DropTable(
                name: "design_contract_versions",
                schema: "app");

            migrationBuilder.DropTable(
                name: "design_system_versions",
                schema: "app");

            migrationBuilder.DropTable(
                name: "feature_inventory_items",
                schema: "app");

            migrationBuilder.DropTable(
                name: "jobs",
                schema: "app");

            migrationBuilder.DropTable(
                name: "projects",
                schema: "app");

            migrationBuilder.DropTable(
                name: "screen_versions",
                schema: "app");

            migrationBuilder.DropTable(
                name: "design_contracts",
                schema: "app");

            migrationBuilder.DropTable(
                name: "design_systems",
                schema: "app");

            migrationBuilder.DropTable(
                name: "feature_inventories",
                schema: "app");

            migrationBuilder.DropTable(
                name: "screens",
                schema: "app");
        }
    }
}
