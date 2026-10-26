using AIDesignStudio.Domain.Assets;
using AIDesignStudio.Domain.DesignContracts;
using AIDesignStudio.Domain.DesignSystems;
using AIDesignStudio.Domain.Jobs;
using AIDesignStudio.Domain.Projects;
using AIDesignStudio.Domain.Redesign;
using AIDesignStudio.Domain.Screens;
using AIDesignStudio.SharedKernel;
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
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
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

        // --- Projects module -------------------------------------------------
        b.Entity<DesignProject>(e =>
        {
            e.ToTable("projects");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();
            e.HasIndex(x => x.OrganizationId);

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
        });

        ConfigureDomainGeneratedKeys(b);

        base.OnModelCreating(b);
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
            if (entityType.FindProperty(nameof(Entity.Id)) is { } id)
            {
                id.ValueGenerated = ValueGenerated.Never;
            }
        }
    }
}
