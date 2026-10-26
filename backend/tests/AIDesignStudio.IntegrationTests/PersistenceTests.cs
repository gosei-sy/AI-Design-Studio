using AIDesignStudio.Domain.DesignContracts;
using AIDesignStudio.Domain.DesignSystems;
using AIDesignStudio.Domain.Jobs;
using AIDesignStudio.Domain.Projects;
using AIDesignStudio.Domain.Redesign;
using AIDesignStudio.Domain.Screens;
using AIDesignStudio.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using Xunit;

namespace AIDesignStudio.IntegrationTests;

/// <summary>
/// Verifies that the domain's invariants survive a real round trip through
/// PostgreSQL, and that the database enforces the ones the aggregate cannot
/// enforce alone under concurrency.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PersistenceTests(DatabaseFixture fixture)
{
    private readonly IClock _clock = new SystemClock();

    private void RequireDatabase() => Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);

    private async Task<Guid> SeedProjectAsync(ProjectKind kind = ProjectKind.NewProduct)
    {
        await using var db = fixture.CreateContext();
        var project = DesignProject.Create(Guid.CreateVersion7(), "Integration Project", kind, _clock);
        db.Projects.Add(project);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return project.Id;
    }

    [Fact]
    public async Task Migrations_produce_every_expected_table()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();

        var tables = await db.Database
            .SqlQuery<string>($"""
                SELECT table_name AS "Value"
                FROM information_schema.tables
                WHERE table_schema = 'app'
                """)
            .ToListAsync(TestContext.Current.CancellationToken);

        tables.ShouldContain("projects");
        tables.ShouldContain("design_contracts");
        tables.ShouldContain("design_contract_versions");
        tables.ShouldContain("design_systems");
        tables.ShouldContain("design_system_versions");
        tables.ShouldContain("screens");
        tables.ShouldContain("screen_versions");
        tables.ShouldContain("feature_inventories");
        tables.ShouldContain("feature_inventory_items");
        tables.ShouldContain("assets");
        tables.ShouldContain("jobs");
    }

    [Fact]
    public async Task A_project_round_trips_with_its_workflow_kind_stored_as_text()
    {
        RequireDatabase();

        var projectId = await SeedProjectAsync(ProjectKind.Redesign);

        await using var db = fixture.CreateContext();
        var loaded = await db.Projects.SingleAsync(p => p.Id == projectId, TestContext.Current.CancellationToken);

        loaded.Kind.ShouldBe(ProjectKind.Redesign);
        loaded.Name.ShouldBe("Integration Project");

        // Enums are persisted as readable text, so the data stays legible in SQL
        // and reordering the enum cannot silently change stored meaning.
        var stored = await db.Database
            .SqlQuery<string>($"""SELECT kind AS "Value" FROM app.projects WHERE id = {projectId}""")
            .SingleAsync(TestContext.Current.CancellationToken);

        stored.ShouldBe("Redesign");
    }

    [Fact]
    public async Task A_versioned_design_contract_round_trips_with_its_approval_metadata()
    {
        RequireDatabase();

        var projectId = await SeedProjectAsync();
        const string payload = """{"screens":["home","checkout"],"a11y":"AA"}""";
        Guid contractId;

        await using (var db = fixture.CreateContext())
        {
            var contract = DesignContract.Create(projectId, _clock);
            contract.StartDraft(payload, _clock);
            contract.ApproveDraft("alice@example.com", _clock);

            db.DesignContracts.Add(contract);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            contractId = contract.Id;
        }

        await using var reader = fixture.CreateContext();
        var loaded = await reader.DesignContracts
            .SingleAsync(c => c.Id == contractId, TestContext.Current.CancellationToken);

        var approved = loaded.ApprovedVersion.ShouldNotBeNull();
        approved.VersionNumber.ShouldBe(1);
        approved.Status.ShouldBe(ContractVersionStatus.Approved);
        approved.ApprovedBy.ShouldBe("alice@example.com");
        approved.PayloadHash.ShouldBe(DesignContractVersion.ComputeHash(payload));
    }

    [Fact]
    public async Task Superseding_a_contract_version_persists_both_versions_with_one_approved()
    {
        RequireDatabase();

        var projectId = await SeedProjectAsync();
        Guid contractId;

        await using (var db = fixture.CreateContext())
        {
            var contract = DesignContract.Create(projectId, _clock);
            contract.StartDraft("""{"v":1}""", _clock);
            contract.ApproveDraft("alice", _clock);
            db.DesignContracts.Add(contract);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            contractId = contract.Id;
        }

        await using (var db = fixture.CreateContext())
        {
            var contract = await db.DesignContracts
                .SingleAsync(c => c.Id == contractId, TestContext.Current.CancellationToken);

            contract.StartDraft("""{"v":2}""", _clock);
            contract.ApproveDraft("bob", _clock);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var reader = fixture.CreateContext();
        var loaded = await reader.DesignContracts
            .SingleAsync(c => c.Id == contractId, TestContext.Current.CancellationToken);

        loaded.Versions.Count.ShouldBe(2);
        loaded.Versions.Count(v => v.Status == ContractVersionStatus.Approved).ShouldBe(1);
        loaded.Versions.Count(v => v.Status == ContractVersionStatus.Superseded).ShouldBe(1);
        loaded.ApprovedVersion!.VersionNumber.ShouldBe(2);
    }

    /// <summary>
    /// The aggregate cannot prevent two concurrent transactions each approving a
    /// different version: both read a state with no approved version. The filtered
    /// unique index is what actually holds the invariant, so it is tested by
    /// bypassing the aggregate and writing the illegal row directly.
    /// </summary>
    [Fact]
    public async Task The_database_rejects_a_second_approved_contract_version()
    {
        RequireDatabase();

        var projectId = await SeedProjectAsync();
        Guid contractId;

        await using (var db = fixture.CreateContext())
        {
            var contract = DesignContract.Create(projectId, _clock);
            contract.StartDraft("""{"v":1}""", _clock);
            contract.ApproveDraft("alice", _clock);
            db.DesignContracts.Add(contract);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            contractId = contract.Id;
        }

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO app.design_contract_versions
                (id, contract_id, version_number, status, payload_json, created_at)
            VALUES
                (@id, @contractId, 2, 'Approved', '{}'::jsonb, now())
            """, connection);

        insert.Parameters.AddWithValue("id", Guid.CreateVersion7());
        insert.Parameters.AddWithValue("contractId", contractId);

        var ex = await Should.ThrowAsync<PostgresException>(() =>
            insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        ex.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        ex.ConstraintName.ShouldBe("ux_contract_single_approved");
    }

    /// <summary>
    /// Idempotency is enforced by a unique index, not only by the pre-insert
    /// lookup, so a race between two API calls cannot create two billable jobs.
    /// </summary>
    [Fact]
    public async Task The_database_rejects_a_duplicate_job_idempotency_key()
    {
        RequireDatabase();

        var organizationId = Guid.CreateVersion7();
        const string key = "screen:checkout:v1";

        await using var db = fixture.CreateContext();
        db.Jobs.Add(JobRecord.Enqueue(organizationId, "screen.generate", "{}", key, _clock));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var second = fixture.CreateContext();
        second.Jobs.Add(JobRecord.Enqueue(organizationId, "screen.generate", "{}", key, _clock));

        var ex = await Should.ThrowAsync<DbUpdateException>(() =>
            second.SaveChangesAsync(TestContext.Current.CancellationToken));

        ex.InnerException.ShouldBeOfType<PostgresException>()
            .SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task The_same_idempotency_key_is_allowed_for_a_different_job_kind()
    {
        RequireDatabase();

        var organizationId = Guid.CreateVersion7();
        const string key = "shared-key";

        await using var db = fixture.CreateContext();
        db.Jobs.Add(JobRecord.Enqueue(organizationId, "screen.generate", "{}", key, _clock));
        db.Jobs.Add(JobRecord.Enqueue(organizationId, "artifact.parse", "{}", key, _clock));

        await Should.NotThrowAsync(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    // -----------------------------------------------------------------------
    // Per-project cardinality (M00-CX-006)
    // -----------------------------------------------------------------------

    /// <summary>
    /// The API is singular — /projects/{id}/contract — and the product calls this
    /// "the" source of truth. With a non-unique project_id, two contracts per
    /// project could each hold an approved version and "which one is in force?"
    /// would have no answer. The per-aggregate approval index cannot express this.
    /// </summary>
    [Fact]
    public async Task A_project_cannot_have_two_design_contracts()
    {
        RequireDatabase();

        var projectId = await SeedProjectAsync();

        await using (var db = fixture.CreateContext())
        {
            db.DesignContracts.Add(DesignContract.Create(projectId, _clock));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var second = fixture.CreateContext();
        second.DesignContracts.Add(DesignContract.Create(projectId, _clock));

        var ex = await Should.ThrowAsync<DbUpdateException>(() =>
            second.SaveChangesAsync(TestContext.Current.CancellationToken));

        ex.InnerException.ShouldBeOfType<PostgresException>()
            .SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task A_project_cannot_have_two_design_systems()
    {
        RequireDatabase();

        var projectId = await SeedProjectAsync();

        await using (var db = fixture.CreateContext())
        {
            db.DesignSystems.Add(DesignSystem.Create(projectId, "Acme", _clock));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var second = fixture.CreateContext();
        second.DesignSystems.Add(DesignSystem.Create(projectId, "Acme Again", _clock));

        var ex = await Should.ThrowAsync<DbUpdateException>(() =>
            second.SaveChangesAsync(TestContext.Current.CancellationToken));

        ex.InnerException.ShouldBeOfType<PostgresException>()
            .SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    // -----------------------------------------------------------------------
    // Design System persistence parity (M00-CX-007)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_versioned_design_system_round_trips_with_its_approval_metadata()
    {
        RequireDatabase();

        var projectId = await SeedProjectAsync();
        const string tokens =
            """
            {"schemaVersion":1,"name":"Acme","typography":{"bodyFont":"Inter","headlineFont":"Manrope"},
             "color":{"roles":{"primary":"#3366ff"}},"shape":{"radiusBase":"8px"}}
            """;
        Guid designSystemId;

        await using (var db = fixture.CreateContext())
        {
            var system = DesignSystem.Create(projectId, "Acme", _clock);
            system.StartDraft(tokens, _clock);
            system.ApproveDraft("alice@example.com", _clock);

            db.DesignSystems.Add(system);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            designSystemId = system.Id;
        }

        await using var reader = fixture.CreateContext();
        var loaded = await reader.DesignSystems
            .SingleAsync(d => d.Id == designSystemId, TestContext.Current.CancellationToken);

        var approved = loaded.ApprovedVersion.ShouldNotBeNull();
        approved.VersionNumber.ShouldBe(1);
        approved.Status.ShouldBe(DesignSystemStatus.Approved);
        approved.ApprovedBy.ShouldBe("alice@example.com");
        approved.TokensHash.ShouldBe(DesignSystemVersion.ComputeHash(tokens));
    }

    [Fact]
    public async Task Superseding_a_design_system_version_persists_both_with_one_approved()
    {
        RequireDatabase();

        var projectId = await SeedProjectAsync();
        Guid designSystemId;

        await using (var db = fixture.CreateContext())
        {
            var system = DesignSystem.Create(projectId, "Acme", _clock);
            system.StartDraft("""{"v":1}""", _clock);
            system.ApproveDraft("alice", _clock);
            db.DesignSystems.Add(system);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            designSystemId = system.Id;
        }

        await using (var db = fixture.CreateContext())
        {
            var system = await db.DesignSystems
                .SingleAsync(d => d.Id == designSystemId, TestContext.Current.CancellationToken);

            system.StartDraft("""{"v":2}""", _clock);
            system.ApproveDraft("bob", _clock);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var reader = fixture.CreateContext();
        var loaded = await reader.DesignSystems
            .SingleAsync(d => d.Id == designSystemId, TestContext.Current.CancellationToken);

        loaded.Versions.Count.ShouldBe(2);
        loaded.Versions.Count(v => v.Status == DesignSystemStatus.Approved).ShouldBe(1);
        loaded.ApprovedVersion!.VersionNumber.ShouldBe(2);
    }

    /// <summary>
    /// The design system's filtered unique index was present in M00 but never
    /// directly proven to reject a second approved row, unlike the contract's.
    /// </summary>
    [Fact]
    public async Task The_database_rejects_a_second_approved_design_system_version()
    {
        RequireDatabase();

        var projectId = await SeedProjectAsync();
        Guid designSystemId;

        await using (var db = fixture.CreateContext())
        {
            var system = DesignSystem.Create(projectId, "Acme", _clock);
            system.StartDraft("""{"v":1}""", _clock);
            system.ApproveDraft("alice", _clock);
            db.DesignSystems.Add(system);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            designSystemId = system.Id;
        }

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO app.design_system_versions
                (id, design_system_id, version_number, status, tokens_json, created_at)
            VALUES
                (@id, @designSystemId, 2, 'Approved', '{}'::jsonb, now())
            """, connection);

        insert.Parameters.AddWithValue("id", Guid.CreateVersion7());
        insert.Parameters.AddWithValue("designSystemId", designSystemId);

        var ex = await Should.ThrowAsync<PostgresException>(() =>
            insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        ex.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        ex.ConstraintName.ShouldBe("ux_design_system_single_approved");
    }

    // -----------------------------------------------------------------------
    // Screen rejection metadata (M00-CX-008)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_screen_rejection_persists_its_reason_reviewer_and_timestamp()
    {
        RequireDatabase();

        var projectId = await SeedProjectAsync();
        var contractVersionId = Guid.CreateVersion7();
        Guid screenId;

        await using (var db = fixture.CreateContext())
        {
            var screen = Screen.Create(projectId, "/checkout", "Checkout", _clock);
            var version = screen.AddVersion(
                ScreenOrigin.ProviderGenerated, contractVersionId, "hash", "google-stitch", "s-1", _clock);

            version.SubmitForReview();
            version.Reject("Contrast ratio below AA on the primary button.", "carol", _clock);

            db.Screens.Add(screen);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            screenId = screen.Id;
        }

        await using var reader = fixture.CreateContext();
        var loaded = await reader.Screens
            .SingleAsync(s => s.Id == screenId, TestContext.Current.CancellationToken);

        var rejected = loaded.Versions.ShouldHaveSingleItem();
        rejected.ReviewState.ShouldBe(ReviewState.Rejected);
        rejected.RejectionReason.ShouldBe("Contrast ratio below AA on the primary button.");
        rejected.RejectedBy.ShouldBe("carol");
        rejected.RejectedAt.ShouldNotBeNull();
    }

    // -----------------------------------------------------------------------
    // ProjectKind check constraint (M00-CX-009)
    // -----------------------------------------------------------------------

    /// <summary>
    /// The domain rejects undefined enum members; this proves the database also
    /// refuses anything that bypassed the domain, since Kind is immutable.
    /// </summary>
    [Fact]
    public async Task The_database_rejects_an_unknown_project_kind()
    {
        RequireDatabase();

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO app.projects (id, organization_id, name, kind, created_at)
            VALUES (@id, @orgId, 'Smuggled', 'Sideways', now())
            """, connection);

        insert.Parameters.AddWithValue("id", Guid.CreateVersion7());
        insert.Parameters.AddWithValue("orgId", Guid.CreateVersion7());

        var ex = await Should.ThrowAsync<PostgresException>(() =>
            insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        ex.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        ex.ConstraintName.ShouldBe("ck_projects_kind");
    }

    // -----------------------------------------------------------------------
    // Coverage round trip (M00-CX-004)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Feature_coverage_round_trips_through_the_private_backing_field()
    {
        RequireDatabase();

        var projectId = await SeedProjectAsync(ProjectKind.Redesign);
        var screenIds = new[] { Guid.CreateVersion7(), Guid.CreateVersion7() };
        Guid inventoryId;

        await using (var db = fixture.CreateContext())
        {
            var inventory = FeatureInventory.Create(projectId, _clock);
            inventory.AddItem("export-csv", "Export CSV", _clock).MarkCovered(screenIds);
            inventory.AddItem("legacy-fax", "Fax", _clock).Waive("Zero usage", "owner@example.com");

            db.FeatureInventories.Add(inventory);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            inventoryId = inventory.Id;
        }

        await using var reader = fixture.CreateContext();
        var loaded = await reader.FeatureInventories
            .SingleAsync(i => i.Id == inventoryId, TestContext.Current.CancellationToken);

        var covered = loaded.Items.Single(i => i.Key == "export-csv");
        covered.Coverage.ShouldBe(CoverageState.Covered);
        covered.CoveringScreenIds.ShouldBe(screenIds, ignoreOrder: true);

        var waived = loaded.Items.Single(i => i.Key == "legacy-fax");
        waived.Coverage.ShouldBe(CoverageState.Waived);
        waived.CoveringScreenIds.ShouldBeEmpty();
        waived.WaivedBy.ShouldBe("owner@example.com");

        Should.NotThrow(loaded.EnsureReadyForHandoff);
    }

    [Fact]
    public async Task A_payload_column_is_stored_as_jsonb_and_is_queryable_as_json()
    {
        RequireDatabase();

        await using var db = fixture.CreateContext();
        db.Jobs.Add(JobRecord.Enqueue(
            Guid.CreateVersion7(),
            "screen.generate",
            """{"prompt":"a login screen","device":"MOBILE"}""",
            $"json-probe-{Guid.CreateVersion7():N}",
            _clock));

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Proves the column is real jsonb rather than text: the ->> operator
        // would fail on a text column.
        var device = await db.Database
            .SqlQuery<string>($"""
                SELECT payload_json ->> 'device' AS "Value"
                FROM app.jobs
                WHERE kind = 'screen.generate'
                ORDER BY created_at DESC
                LIMIT 1
                """)
            .SingleAsync(TestContext.Current.CancellationToken);

        device.ShouldBe("MOBILE");
    }
}
