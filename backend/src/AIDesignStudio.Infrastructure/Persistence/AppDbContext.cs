using AIDesignStudio.Application.Organizations;
using AIDesignStudio.Domain.Assets;
using AIDesignStudio.Domain.DesignContracts;
using AIDesignStudio.Domain.DesignSystems;
using AIDesignStudio.Domain.Jobs;
using AIDesignStudio.Domain.Organizations;
using AIDesignStudio.Domain.Projects;
using AIDesignStudio.Domain.Redesign;
using AIDesignStudio.Domain.Screens;
using AIDesignStudio.Infrastructure.Identity;
using AIDesignStudio.Infrastructure.Organizations;
using AIDesignStudio.SharedKernel;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AIDesignStudio.Infrastructure.Persistence;

/// <summary>
/// Single database context for the modular monolith.
///
/// One context, one transaction boundary, one migration history. Module
/// separation is enforced by schema namespacing and by the architecture tests in
/// <c>AIDesignStudio.ArchitectureTests</c> rather than by separate contexts,
/// which would give up atomic enqueue-with-state-change (see ADR-0005).
///
/// <para>Identity and Data Protection keys live here too, in the <c>auth</c>
/// schema. A second context would mean a second migration history and would give
/// up the ability to create an account and its organization-side state in one
/// transaction — the same reasoning ADR-0005 used for the job queue. Schema
/// namespacing keeps the boundary visible (ADR-0008).</para>
/// </summary>
public sealed class AppDbContext : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>,
    IDataProtectionKeyContext
{
    private readonly IOrganizationScope _scope;

    /// <param name="scope">
    /// The data scope for this unit of work, read by the organization query
    /// filters below.
    ///
    /// <para><b>Omitting it means "no organization", not "every
    /// organization".</b> A context built without a scope sees no
    /// organization-scoped row at all. That default is deliberate and is the
    /// whole safety property: forgetting to supply a scope produces an empty
    /// result set, which is obvious and harmless, rather than a cross-tenant read,
    /// which is neither. The worker and the provisioning tool opt into
    /// <see cref="Organizations.SystemOrganizationScope"/> explicitly.</para>
    /// </param>
    public AppDbContext(DbContextOptions<AppDbContext> options, IOrganizationScope? scope = null)
        : base(options) => _scope = scope ?? ClosedOrganizationScope.Instance;

    /// <summary>
    /// The organization in force. Referenced by the query filters, so EF passes
    /// it as a query parameter on every organization-scoped query.
    /// </summary>
    public Guid? ScopedOrganizationId => _scope.OrganizationId;

    /// <summary>True when this context is deliberately unscoped.</summary>
    public bool IsSystemScope => _scope.IsSystem;

    /// <summary>
    /// Data Protection keys, persisted so cookie sessions survive a restart and
    /// remain valid across instances. Without this, keys live in the container's
    /// filesystem and every deployment silently signs everyone out — and a
    /// second instance cannot read the first's cookies at all.
    /// </summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    /// <summary>
    /// Organizations — the tenant boundary (ADR-0009). In the <c>auth</c> schema
    /// with the rest of the authorization data, not in <c>app</c>: a membership
    /// row is a grant of access, and keeping grants beside credentials is what
    /// makes the authorization boundary legible in the database itself.
    /// </summary>
    public DbSet<Organization> Organizations => Set<Organization>();

    public DbSet<OrganizationMembership> OrganizationMemberships => Set<OrganizationMembership>();

    public DbSet<DesignProject> Projects => Set<DesignProject>();
    public DbSet<DesignContract> DesignContracts => Set<DesignContract>();
    public DbSet<DesignSystem> DesignSystems => Set<DesignSystem>();
    public DbSet<Screen> Screens => Set<Screen>();
    public DbSet<FeatureInventory> FeatureInventories => Set<FeatureInventory>();
    public DbSet<AssetReference> Assets => Set<AssetReference>();
    public DbSet<JobRecord> Jobs => Set<JobRecord>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("app");

        ConfigureOrganizations(b);

        // --- Projects module -------------------------------------------------
        b.Entity<DesignProject>(e =>
        {
            e.ToTable("projects");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();
            e.HasQueryFilter(x => IsSystemScope || x.OrganizationId == ScopedOrganizationId);

            // The principal key every project-owned child points at (W3-RR-001).
            //
            // (organization_id, id) is trivially unique because `id` alone is the
            // primary key — declaring it an alternate key is what lets a child
            // reference the *pair*, so the database can refuse a child whose
            // tenant key disagrees with its project's.
            //
            // Its unique index leads with organization_id, so it serves every
            // query ix_projects_organization_id served. That index is dropped
            // rather than left duplicating this one.
            e.HasAlternateKey(x => new { x.OrganizationId, x.Id })
                .HasName("ak_projects_organization_id_id");

            // Computed from OrganizationId + Id; never a column.
            e.Ignore(x => x.Ownership);

            // Kind is immutable and drives workflow gating, so an out-of-range
            // value would be permanently wrong. The domain rejects undefined enum
            // members; this stops anything that bypasses the domain.
            e.ToTable(t => t.HasCheckConstraint(
                "ck_projects_kind", "kind IN ('NewProduct', 'Redesign')"));
        });

        // --- DesignContracts module ------------------------------------------
        b.Entity<DesignContract>(e =>
        {
            e.ToTable("design_contracts");
            e.HasKey(x => x.Id);

            // One contract per project. The API is singular
            // (/projects/{id}/contract) and the product calls this "the" source of
            // truth, so two contracts per project would make "which one is in
            // force?" unanswerable. The per-aggregate approval index below cannot
            // express this: it allows one approved version per contract, not one
            // contract per project.
            e.HasIndex(x => x.ProjectId).IsUnique();
            e.HasQueryFilter(x => IsSystemScope || x.OrganizationId == ScopedOrganizationId);

            // Tenant ownership, enforced by the database (W3-RR-001).
            //
            // The re-review inserted a row carrying organization A's tenant key
            // on organization B's project and PostgreSQL accepted it; the global
            // filter then served that row as A's data. Referencing the *pair*
            // makes the two assertions agree or the write fail.
            //
            // No navigation property: Domain modules must not reference each
            // other's types, and Domain_modules_do_not_reference_each_others_types
            // enforces it. The relationship is declared by foreign key only.
            //
            // Restrict rather than Cascade: no delete path exists yet, and a
            // future one should have to decide what happens to children rather
            // than inherit a silent cascade.
            e.HasOne<DesignProject>()
                .WithMany()
                .HasForeignKey(x => new { x.OrganizationId, x.ProjectId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
                .HasConstraintName("fk_design_contracts_project_tenant")
                .OnDelete(DeleteBehavior.Restrict);

            e.OwnsMany(x => x.Versions, v =>
            {
                v.ToTable("design_contract_versions");
                v.WithOwner().HasForeignKey(x => x.ContractId);
                v.HasKey(x => x.Id);
                v.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
                v.Property(x => x.PayloadJson).HasColumnType("jsonb").IsRequired();
                v.Property(x => x.PayloadHash).HasMaxLength(64);
                v.Property(x => x.ApprovedBy).HasMaxLength(256);

                // Version numbers are dense and monotonic per contract.
                v.HasIndex(x => new { x.ContractId, x.VersionNumber }).IsUnique();

                // At most one Approved version per contract, enforced in the
                // database as well as in the aggregate: a filtered unique index
                // is the only thing that holds under concurrent approvals.
                v.HasIndex(x => x.ContractId)
                    .HasDatabaseName("ux_contract_single_approved")
                    .IsUnique()
                    .HasFilter("status = 'Approved'");
            });
            e.Navigation(x => x.Versions).AutoInclude();

            // Computed lookups over Versions, not relationships.
            e.Ignore(x => x.ApprovedVersion);
            e.Ignore(x => x.DraftVersion);
        });

        // --- DesignSystems module --------------------------------------------
        b.Entity<DesignSystem>(e =>
        {
            e.ToTable("design_systems");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();

            // One design system per project, for the same reason as contracts.
            e.HasIndex(x => x.ProjectId).IsUnique();
            e.HasQueryFilter(x => IsSystemScope || x.OrganizationId == ScopedOrganizationId);

            // Tenant ownership, enforced by the database (W3-RR-001).
            //
            // The re-review inserted a row carrying organization A's tenant key
            // on organization B's project and PostgreSQL accepted it; the global
            // filter then served that row as A's data. Referencing the *pair*
            // makes the two assertions agree or the write fail.
            //
            // No navigation property: Domain modules must not reference each
            // other's types, and Domain_modules_do_not_reference_each_others_types
            // enforces it. The relationship is declared by foreign key only.
            //
            // Restrict rather than Cascade: no delete path exists yet, and a
            // future one should have to decide what happens to children rather
            // than inherit a silent cascade.
            e.HasOne<DesignProject>()
                .WithMany()
                .HasForeignKey(x => new { x.OrganizationId, x.ProjectId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
                .HasConstraintName("fk_design_systems_project_tenant")
                .OnDelete(DeleteBehavior.Restrict);


            e.OwnsMany(x => x.Versions, v =>
            {
                v.ToTable("design_system_versions");
                v.WithOwner().HasForeignKey(x => x.DesignSystemId);
                v.HasKey(x => x.Id);
                v.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
                v.Property(x => x.TokensJson).HasColumnType("jsonb").IsRequired();
                v.Property(x => x.TokensHash).HasMaxLength(64);
                v.Property(x => x.ApprovedBy).HasMaxLength(256);
                v.HasIndex(x => new { x.DesignSystemId, x.VersionNumber }).IsUnique();
                v.HasIndex(x => x.DesignSystemId)
                    .HasDatabaseName("ux_design_system_single_approved")
                    .IsUnique()
                    .HasFilter("status = 'Approved'");
            });
            e.Navigation(x => x.Versions).AutoInclude();

            e.Ignore(x => x.ApprovedVersion);
            e.Ignore(x => x.DraftVersion);
        });

        // --- Screens module ---------------------------------------------------
        b.Entity<Screen>(e =>
        {
            e.ToTable("screens");
            e.HasKey(x => x.Id);
            e.Property(x => x.Key).HasMaxLength(256).IsRequired();
            e.Property(x => x.Title).HasMaxLength(256).IsRequired();

            // A screen key identifies a screen within its project.
            e.HasIndex(x => new { x.ProjectId, x.Key }).IsUnique();
            e.HasQueryFilter(x => IsSystemScope || x.OrganizationId == ScopedOrganizationId);

            // Tenant ownership, enforced by the database (W3-RR-001).
            //
            // The re-review inserted a row carrying organization A's tenant key
            // on organization B's project and PostgreSQL accepted it; the global
            // filter then served that row as A's data. Referencing the *pair*
            // makes the two assertions agree or the write fail.
            //
            // No navigation property: Domain modules must not reference each
            // other's types, and Domain_modules_do_not_reference_each_others_types
            // enforces it. The relationship is declared by foreign key only.
            //
            // Restrict rather than Cascade: no delete path exists yet, and a
            // future one should have to decide what happens to children rather
            // than inherit a silent cascade.
            e.HasOne<DesignProject>()
                .WithMany()
                .HasForeignKey(x => new { x.OrganizationId, x.ProjectId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
                .HasConstraintName("fk_screens_project_tenant")
                .OnDelete(DeleteBehavior.Restrict);


            e.OwnsMany(x => x.Versions, v =>
            {
                v.ToTable("screen_versions");
                v.WithOwner().HasForeignKey(x => x.ScreenId);
                v.HasKey(x => x.Id);
                v.Property(x => x.Origin).HasConversion<string>().HasMaxLength(32).IsRequired();
                v.Property(x => x.ReviewState).HasConversion<string>().HasMaxLength(32).IsRequired();
                v.Property(x => x.ContractPayloadHash).HasMaxLength(64).IsRequired();
                v.Property(x => x.ProviderId).HasMaxLength(64);
                v.Property(x => x.ProviderArtifactId).HasMaxLength(256);
                v.Property(x => x.ApprovedBy).HasMaxLength(256);
                v.Property(x => x.RejectionReason).HasMaxLength(4000);
                v.Property(x => x.RejectedBy).HasMaxLength(256);
                v.HasIndex(x => new { x.ScreenId, x.VersionNumber }).IsUnique();
                v.HasIndex(x => x.ScreenId)
                    .HasDatabaseName("ux_screen_single_approved")
                    .IsUnique()
                    .HasFilter("review_state = 'Approved'");
            });
            e.Navigation(x => x.Versions).AutoInclude();

            e.Ignore(x => x.ApprovedVersion);
        });

        // --- Redesign module --------------------------------------------------
        b.Entity<FeatureInventory>(e =>
        {
            e.ToTable("feature_inventories");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ProjectId).IsUnique();
            e.HasQueryFilter(x => IsSystemScope || x.OrganizationId == ScopedOrganizationId);

            // Tenant ownership, enforced by the database (W3-RR-001).
            //
            // The re-review inserted a row carrying organization A's tenant key
            // on organization B's project and PostgreSQL accepted it; the global
            // filter then served that row as A's data. Referencing the *pair*
            // makes the two assertions agree or the write fail.
            //
            // No navigation property: Domain modules must not reference each
            // other's types, and Domain_modules_do_not_reference_each_others_types
            // enforces it. The relationship is declared by foreign key only.
            //
            // Restrict rather than Cascade: no delete path exists yet, and a
            // future one should have to decide what happens to children rather
            // than inherit a silent cascade.
            e.HasOne<DesignProject>()
                .WithMany()
                .HasForeignKey(x => new { x.OrganizationId, x.ProjectId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
                .HasConstraintName("fk_feature_inventories_project_tenant")
                .OnDelete(DeleteBehavior.Restrict);

            e.OwnsMany(x => x.Items, i =>
            {
                i.ToTable("feature_inventory_items");
                i.WithOwner().HasForeignKey(x => x.InventoryId);
                i.HasKey(x => x.Id);
                i.Property(x => x.Key).HasMaxLength(256).IsRequired();
                i.Property(x => x.Description).HasMaxLength(2000);
                i.Property(x => x.Coverage).HasConversion<string>().HasMaxLength(32).IsRequired();
                i.Property(x => x.WaiverReason).HasMaxLength(2000);
                i.Property(x => x.WaivedBy).HasMaxLength(256);

                // Mapped through the backing field: the public property is a
                // read-only wrapper so callers cannot mutate coverage directly.
                i.Property<List<Guid>>("_coveringScreenIds")
                    .HasColumnName("covering_screen_ids")
                    .HasColumnType("uuid[]");
                i.HasIndex(x => new { x.InventoryId, x.Key }).IsUnique();
            });
            e.Navigation(x => x.Items).AutoInclude();

            e.Ignore(x => x.BlockingItems);
        });

        // --- Assets module ----------------------------------------------------
        b.Entity<AssetReference>(e =>
        {
            e.ToTable("assets");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();
            e.Property(x => x.StorageKey).HasMaxLength(512).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(128).IsRequired();
            e.Property(x => x.ContentHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.OriginalFileName).HasMaxLength(512);
            e.HasIndex(x => x.StorageKey).IsUnique();
            e.HasIndex(x => new { x.OrganizationId, x.ProjectId });
            e.HasQueryFilter(x => IsSystemScope || x.OrganizationId == ScopedOrganizationId);

            // Tenant ownership, enforced by the database (W3-RR-001).
            //
            // The re-review inserted a row carrying organization A's tenant key
            // on organization B's project and PostgreSQL accepted it; the global
            // filter then served that row as A's data. Referencing the *pair*
            // makes the two assertions agree or the write fail.
            //
            // No navigation property: Domain modules must not reference each
            // other's types, and Domain_modules_do_not_reference_each_others_types
            // enforces it. The relationship is declared by foreign key only.
            //
            // Restrict rather than Cascade: no delete path exists yet, and a
            // future one should have to decide what happens to children rather
            // than inherit a silent cascade.
            e.HasOne<DesignProject>()
                .WithMany()
                .HasForeignKey(x => new { x.OrganizationId, x.ProjectId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
                .HasConstraintName("fk_assets_project_tenant")
                .OnDelete(DeleteBehavior.Restrict);

        });

        // --- Jobs module ------------------------------------------------------
        b.Entity<JobRecord>(e =>
        {
            e.ToTable("jobs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasMaxLength(64).IsRequired();
            e.Property(x => x.PayloadJson).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.IdempotencyKey).HasMaxLength(256).IsRequired();
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(32).IsRequired();
            e.Property(x => x.LeaseOwner).HasMaxLength(128);
            e.Property(x => x.LastError).HasMaxLength(4000);

            // Collapses duplicate submissions of the same work onto one job.
            //
            // Scoped by organization because idempotency keys are client-supplied:
            // a global unique index would let one tenant's key collide with
            // another's, and the pre-insert lookup would then return a job
            // belonging to a different organization.
            e.HasIndex(x => new { x.OrganizationId, x.Kind, x.IdempotencyKey }).IsUnique();

            // Supports organization-scoped job queries at the API boundary.
            e.HasIndex(x => x.OrganizationId);

            // Supports the claim query: eligible jobs ordered by readiness.
            e.HasIndex(x => new { x.State, x.RunAfter });

            // Scoped like every other tenant table. The worker runs system-scoped
            // because it dispatches work for all tenants; an API request that
            // enqueues or reads a job sees only its own organization's.
            //
            // The claim statement is raw SQL and is therefore NOT affected by this
            // filter — which is correct (the dispatcher must see every tenant's
            // queue) and worth stating, because it means this filter is not what
            // protects the claim path. The claim is protected by running only in
            // the worker.
            e.HasQueryFilter(x => IsSystemScope || x.OrganizationId == ScopedOrganizationId);
        });

        // Identity's own model configuration. Must run before the table mapping
        // below, which moves what it just configured into the auth schema.
        base.OnModelCreating(b);

        ConfigureIdentitySchema(b);
        ConfigureDomainGeneratedKeys(b);
    }

    /// <summary>
    /// Organizations and memberships (ADR-0009, D-9).
    ///
    /// <para>Every invariant the aggregate enforces is enforced here too. That is
    /// not belt-and-braces: an authorization row can be written by a migration, a
    /// psql session or a future code path that bypasses the aggregate, and a
    /// membership with zero roles or two active memberships for one user would
    /// change who can approve. The aggregate is where the error message lives; the
    /// database is where the guarantee lives.</para>
    /// </summary>
    private static void ConfigureOrganizations(ModelBuilder b)
    {
        b.Entity<Organization>(e =>
        {
            e.ToTable("organizations", "auth");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();

            // Operator provisioning selects an organization by name, so two
            // organizations with the same name would make "--organization Acme"
            // ambiguous — and the operator would not know which tenant they just
            // granted access to.
            e.HasIndex(x => x.Name).IsUnique().HasDatabaseName("ux_organizations_name");

            // NOT organization-filtered, and deliberately so. Its key *is* the
            // organization id, so a filter would read `id = @scope` — which is
            // what the endpoint comparison already does, explicitly and before
            // any query runs. Cross-organization reads are refused in
            // OrganizationEndpoints by comparing the route value with the
            // resolved scope, which is cheaper and easier to read than a filter.
        });

        b.Entity<OrganizationMembership>(e =>
        {
            e.ToTable("organization_memberships", "auth");
            e.HasKey(x => x.Id);

            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            e.Property(x => x.ProvisionedBy).HasMaxLength(256).IsRequired();
            e.Property(x => x.RolesChangedBy).HasMaxLength(256);
            e.Property(x => x.RevokedBy).HasMaxLength(256);

            // Roles as an integer flags column rather than this codebase's usual
            // string enum. A flags value has no single valid string form
            // ("Member, Approver" would have to be parsed), and the integer lets
            // the two invariants below be check constraints instead of prose.
            e.Property(x => x.Roles).HasConversion<int>().IsRequired();

            e.HasIndex(x => new { x.OrganizationId, x.Status });

            // **Exactly one active membership per user** (ADR-0009). A filtered
            // unique index, for the same reason the single-approved-version rule
            // is one: an aggregate check cannot hold under two concurrent
            // provisioning runs, and this is the invariant that decides which
            // tenant's data a session can reach.
            //
            // Filtered on the status value rather than declared across
            // (user_id, status), so revoked rows stay unconstrained — a user may
            // accumulate any number of historical memberships.
            e.HasIndex(x => x.UserId)
                .HasDatabaseName("ux_organization_memberships_active_user")
                .IsUnique()
                .HasFilter("status = 'Active'");

            e.ToTable(t =>
            {
                // At least one role, and no bits this application does not define.
                // Member = 1, Approver = 2, so 1..3 is the whole legal range;
                // 0 is "grants nothing" and 4+ is a cast integer or a value from
                // a future version.
                t.HasCheckConstraint(
                    "ck_organization_memberships_roles", "roles > 0 AND roles <= 3");

                t.HasCheckConstraint(
                    "ck_organization_memberships_status",
                    "status IN ('Active', 'Revoked')");

                // A revoked membership must say who revoked it and when; an
                // active one must claim neither.
                //
                // Two constraints rather than one disjunction, so that each
                // failure names exactly one cause. A single
                // "(Revoked AND ...) OR (Active AND ...)" rule is also violated
                // by an unknown status, which would make a status error and an
                // audit error indistinguishable in the message the operator sees.
                t.HasCheckConstraint(
                    "ck_organization_memberships_revoked_audit",
                    "status <> 'Revoked' OR (revoked_at IS NOT NULL AND revoked_by IS NOT NULL)");

                t.HasCheckConstraint(
                    "ck_organization_memberships_active_audit",
                    "status <> 'Active' OR (revoked_at IS NULL AND revoked_by IS NULL)");

                // A role change records who and when, or neither (W3-002).
                t.HasCheckConstraint(
                    "ck_organization_memberships_role_change_audit",
                    "(roles_changed_at IS NULL) = (roles_changed_by IS NULL)");
            });

            // Membership belongs to an authenticated user — enforced by the
            // database, not by hope. Without this FK a membership could be
            // written for a user id that never existed, and that row would grant
            // organization access to whoever later received the id.
            //
            // No navigation property: Domain must not see an Identity type, so
            // the relationship is declared here by foreign key only.
            e.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .HasConstraintName("fk_organization_memberships_user")
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(x => x.OrganizationId)
                .HasConstraintName("fk_organization_memberships_organization")
                .OnDelete(DeleteBehavior.Restrict);

            // The append-only audit chain (W3-002). An owned collection, so it is
            // part of the membership's consistency boundary: the aggregate writes
            // an entry in the same SaveChanges as the state change it records.
            //
            // Deliberately NOT AutoInclude'd, unlike contract and screen versions.
            // The authorization path reads a membership on every request and never
            // needs its history; loading the chain each time would make the
            // hottest query in the application grow without bound.
            e.OwnsMany(x => x.Events, ev =>
            {
                ev.ToTable("organization_membership_events", "auth");
                ev.WithOwner().HasForeignKey(x => x.MembershipId);
                ev.HasKey(x => x.Id);
                ev.Property(x => x.Type).HasConversion<string>().HasMaxLength(32).IsRequired();
                ev.Property(x => x.PreviousRoles).HasConversion<int?>();
                ev.Property(x => x.NewRoles).HasConversion<int?>();
                ev.Property(x => x.Actor).HasMaxLength(256).IsRequired();

                // Reading one membership's history, newest or oldest first.
                ev.HasIndex(x => new { x.MembershipId, x.OccurredAt });

                // Reading every privileged change in one tenant, or for one user,
                // without joining back through the membership — which is the point
                // of denormalizing both ids onto the entry.
                ev.HasIndex(x => new { x.OrganizationId, x.OccurredAt });
                ev.HasIndex(x => x.UserId);

                ev.ToTable(t => t.HasCheckConstraint(
                    "ck_organization_membership_events_type",
                    "type IN ('Provisioned', 'RolesChanged', 'Revoked', 'MigrationBaseline')"));
            });

            e.Ignore(x => x.IsActive);
            e.Ignore(x => x.CanApprove);
        });
    }

    /// <summary>
    /// Puts Identity and Data Protection tables in the <c>auth</c> schema.
    ///
    /// The default schema is <c>app</c>, which is the product's data. Keeping
    /// credentials, security stamps and key material in a separate schema makes
    /// the boundary legible in the database itself, and means a future read-only
    /// grant on <c>app</c> does not accidentally include the user table.
    /// </summary>
    private static void ConfigureIdentitySchema(ModelBuilder b)
    {
        b.Entity<ApplicationUser>().ToTable("users", "auth");
        b.Entity<ApplicationRole>().ToTable("roles", "auth");
        b.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims", "auth");
        b.Entity<IdentityUserRole<Guid>>().ToTable("user_roles", "auth");
        b.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins", "auth");
        b.Entity<IdentityRoleClaim<Guid>>().ToTable("role_claims", "auth");
        b.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens", "auth");
        b.Entity<DataProtectionKey>().ToTable("data_protection_keys", "auth");
    }

    /// <summary>
    /// Declares every <see cref="Entity.Id"/> as domain-generated rather than
    /// store-generated.
    ///
    /// This is a correctness fix, not a tuning choice. <see cref="Entity"/>
    /// assigns a UUID v7 on construction, but EF's convention for a Guid key is
    /// "generated on add", and EF then uses a non-default key value to infer that
    /// an entity already exists. A version newly added to an aggregate's owned
    /// collection was therefore tracked as Modified, producing an UPDATE that
    /// matched no row (DbUpdateConcurrencyException) instead of an INSERT.
    ///
    /// Covered by
    /// <c>PersistenceTests.Superseding_a_contract_version_persists_both_versions_with_one_approved</c>.
    /// </summary>
    private static void ConfigureDomainGeneratedKeys(ModelBuilder b)
    {
        foreach (var entityType in b.Model.GetEntityTypes())
        {
            // Restricted to Entity subclasses. It used to apply to every type
            // with an Id property, which was harmless while the model held only
            // domain aggregates — and became a defect the moment Identity
            // arrived: IdentityUserClaim.Id is an int identity column, and
            // declaring it never-store-generated makes every insert write 0 and
            // collide. The narrower rule also matches what the convention is
            // actually for (DATA_MODEL §9).
            if (!typeof(Entity).IsAssignableFrom(entityType.ClrType))
            {
                continue;
            }

            if (entityType.FindProperty(nameof(Entity.Id)) is { } id)
            {
                id.ValueGenerated = ValueGenerated.Never;
            }
        }
    }
}
