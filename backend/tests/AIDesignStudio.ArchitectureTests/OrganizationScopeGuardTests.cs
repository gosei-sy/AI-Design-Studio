using AIDesignStudio.Domain.Organizations;
using AIDesignStudio.Infrastructure.Identity;
using AIDesignStudio.Infrastructure.Organizations;
using AIDesignStudio.Infrastructure.Persistence;
using AIDesignStudio.SharedKernel;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Shouldly;
using Xunit;

namespace AIDesignStudio.ArchitectureTests;

/// <summary>
/// The guard over **every organization-scoped query path** (M01-03, ADR-0009).
///
/// <para><b>Rewritten after W3-001.</b> The first version discovered the set to
/// check by looking for a direct <c>OrganizationId</c> property, and then
/// asserted that every member of that set had a filter. It passed — while
/// <c>DesignContract</c>, <c>DesignSystem</c>, <c>Screen</c> and
/// <c>FeatureInventory</c> were tenant-owned through <c>ProjectId</c>, had no
/// filter, and were invisible to the test. **The discovery step was the
/// defect:** a guard that chooses its own population can only ever confirm the
/// author's model of what exists.</para>
///
/// <para>This version takes the whole EF model and **partitions** it. Every
/// entity type must fall into exactly one justified category, and anything that
/// matches none fails. A table added in M02 cannot be missed, because being
/// unclassified is itself a failure — the test does not have to know it
/// exists.</para>
/// </summary>
public sealed class OrganizationScopeGuardTests
{
    /// <summary>
    /// The only entity types permitted to be unscoped, each with its reason.
    ///
    /// <para>Listed by CLR type so adding a member is a deliberate, reviewable
    /// act, and asserted by value below so the list cannot quietly grow.</para>
    /// </summary>
    private static readonly Dictionary<Type, string> UnscopedByDesign = new()
    {
        // The tenant itself. Its primary key *is* the organization id, so a
        // filter would read `id = @scope` — which is what
        // OrganizationEndpoints already does explicitly, before any query runs.
        [typeof(Organization)] = "its key is the tenant id",

        // What produces the scope. Filtering it would be circular: the resolver
        // could never read the row that says which organization to filter by.
        // Its one query carries its own predicate, asserted by
        // AuthorizationTests.An_approver_cannot_list_another_organizations_members.
        [typeof(OrganizationMembership)] = "it produces the scope",

        // Identity and Data Protection. Credentials and key material are not
        // tenant data; a user exists before any membership does, and the key ring
        // protects every tenant's cookies.
        [typeof(ApplicationUser)] = "identity, not tenant data",
        [typeof(ApplicationRole)] = "identity, not tenant data",
        [typeof(IdentityUserClaim<Guid>)] = "identity, not tenant data",
        [typeof(IdentityUserRole<Guid>)] = "identity, not tenant data",
        [typeof(IdentityUserLogin<Guid>)] = "identity, not tenant data",
        [typeof(IdentityRoleClaim<Guid>)] = "identity, not tenant data",
        [typeof(IdentityUserToken<Guid>)] = "identity, not tenant data",
        [typeof(DataProtectionKey)] = "key material, not tenant data",
    };

    private static IModel BuildModel()
    {
        // A connection string is required to build the model but never opened.
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=model-only;Database=model-only")
            .UseSnakeCaseNamingConvention()
            .Options;

        using var context = new AppDbContext(options, ClosedOrganizationScope.Instance);

        return context.Model;
    }

    /// <summary>
    /// The root an entity is reached through: itself, or the owner it is part of.
    /// An owned collection has no independent query path, so its isolation is
    /// whatever its root's is.
    /// </summary>
    private static IEntityType RootOf(IEntityType entityType)
    {
        var current = entityType;

        while (current.IsOwned() && current.FindOwnership()?.PrincipalEntityType is { } owner)
        {
            current = owner;
        }

        return current;
    }

    private static bool IsFiltered(IEntityType entityType) =>
        entityType.GetDeclaredQueryFilters().Count > 0;

    // =======================================================================
    // The partition
    // =======================================================================

    /// <summary>
    /// Every entity type in the model is either organization-filtered, owned by
    /// something that is, or explicitly exempt. Nothing may be none of those.
    ///
    /// <para>This is the assertion W3-001 needed: it covers types the author did
    /// not think about, because an unclassified type fails rather than being
    /// skipped.</para>
    /// </summary>
    [Fact]
    public void Every_entity_in_the_model_is_filtered_owned_or_explicitly_exempt()
    {
        var model = BuildModel();
        var unclassified = new List<string>();

        foreach (var entityType in model.GetEntityTypes())
        {
            var root = RootOf(entityType);

            if (IsFiltered(root) || UnscopedByDesign.ContainsKey(root.ClrType))
            {
                continue;
            }

            unclassified.Add(entityType.IsOwned()
                ? $"{entityType.ClrType.Name} (owned by {root.ClrType.Name})"
                : entityType.ClrType.Name);
        }

        unclassified.ShouldBeEmpty(
            "every entity must be organization-filtered, owned by something that is, or listed in " +
            "UnscopedByDesign with a reason. Unclassified: " + string.Join(", ", unclassified));
    }

    /// <summary>
    /// Every tenant-owned aggregate root carries the tenant key **directly**.
    ///
    /// <para>This is what makes the filter uniform. Tenancy is transitive for
    /// contracts, design systems, screens and inventories — they belong to a
    /// project, which belongs to an organization — and W3-001's correction was to
    /// denormalize the key rather than filter through a correlated subquery on
    /// every read. An aggregate added later without the key fails here.</para>
    /// </summary>
    [Fact]
    public void Every_tenant_owned_aggregate_root_carries_the_tenant_key()
    {
        var domain = typeof(Organization).Assembly;

        var missing = domain.GetTypes()
            .Where(t => typeof(AggregateRoot).IsAssignableFrom(t) && !t.IsAbstract)
            .Where(t => !UnscopedByDesign.ContainsKey(t))
            .Where(t => t.GetProperty("OrganizationId")?.PropertyType != typeof(Guid))
            .Select(t => t.Name)
            .ToList();

        missing.ShouldBeEmpty(
            "every aggregate root is tenant data unless it is listed in UnscopedByDesign. " +
            "Missing a Guid OrganizationId: " + string.Join(", ", missing));
    }

    /// <summary>
    /// And every one of those roots is actually filtered in the model. The
    /// previous test proves the key exists; this proves it is used.
    /// </summary>
    [Fact]
    public void Every_aggregate_root_carrying_the_tenant_key_is_filtered()
    {
        var model = BuildModel();

        var unguarded = model.GetEntityTypes()
            .Where(e => !e.IsOwned())
            .Where(e => !UnscopedByDesign.ContainsKey(e.ClrType))
            .Where(e => e.FindProperty("OrganizationId") is { } p && p.ClrType == typeof(Guid))
            .Where(e => !IsFiltered(e))
            .Select(e => e.ClrType.Name)
            .ToList();

        unguarded.ShouldBeEmpty(
            "a cross-tenant read is one forgotten Where clause away without a global filter. " +
            "Unguarded: " + string.Join(", ", unguarded));
    }

    /// <summary>
    /// The seven tenant tables, named. A belt-and-braces check against the
    /// partition above silently emptying out — if someone exempted everything,
    /// the other tests would pass and this one would not.
    /// </summary>
    [Fact]
    public void All_seven_known_tenant_tables_are_filtered()
    {
        var model = BuildModel();

        string[] expected =
        [
            "DesignProject", "DesignContract", "DesignSystem", "Screen",
            "FeatureInventory", "AssetReference", "JobRecord",
        ];

        var filtered = model.GetEntityTypes()
            .Where(IsFiltered)
            .Select(e => e.ClrType.Name)
            .ToList();

        foreach (var name in expected)
        {
            filtered.ShouldContain(name, $"{name} is tenant data and must be organization-filtered");
        }
    }

    // =======================================================================
    // Tenant ownership is referential, not only filtered (W3-RR-001)
    // =======================================================================

    /// <summary>
    /// The five roots that are tenant-owned **through a project**. Asserted by
    /// value below, so a sixth cannot appear without a deliberate edit.
    /// </summary>
    private static readonly string[] ProjectOwnedRoots =
        ["AssetReference", "DesignContract", "DesignSystem", "FeatureInventory", "Screen"];

    private static List<IEntityType> ProjectOwnedTypes(IModel model) =>
        [.. model.GetEntityTypes()
            .Where(e => !e.IsOwned())
            .Where(e => e.FindProperty("OrganizationId") is { } o && o.ClrType == typeof(Guid))
            .Where(e => e.FindProperty("ProjectId") is { } p && p.ClrType == typeof(Guid))];

    /// <summary>
    /// Every project-owned root must tie <c>(OrganizationId, ProjectId)</c> to
    /// its project's <c>(OrganizationId, Id)</c>.
    ///
    /// <para><b>Why a filter was not enough.</b> Denormalizing the tenant key
    /// created a second ownership assertion, and the re-review proved PostgreSQL
    /// would accept the two disagreeing: organization A's key on organization B's
    /// project. The filter then classifies that row as A's. A query filter cannot
    /// repair contradictory ownership data after it is written — only a
    /// constraint can stop it being written.</para>
    ///
    /// <para>This is the guard the ADR's "cannot drift" sentence stood in for.
    /// It was true of mutation and false of creation.</para>
    /// </summary>
    [Fact]
    public void Every_project_owned_root_is_tied_to_its_projects_tenant()
    {
        var model = BuildModel();
        var unconstrained = new List<string>();

        foreach (var entityType in ProjectOwnedTypes(model))
        {
            var tied = entityType.GetForeignKeys().Any(fk =>
                fk.PrincipalEntityType.ClrType.Name == "DesignProject"
                && fk.Properties.Select(p => p.Name).SequenceEqual(["OrganizationId", "ProjectId"])
                && fk.PrincipalKey.Properties.Select(p => p.Name)
                    .SequenceEqual(["OrganizationId", "Id"]));

            if (!tied)
            {
                unconstrained.Add(entityType.ClrType.Name);
            }
        }

        unconstrained.ShouldBeEmpty(
            "a denormalized tenant key needs a composite foreign key, not a comment: without one " +
            "the database accepts one organization's key on another's project. Unconstrained: " +
            string.Join(", ", unconstrained));
    }

    /// <summary>
    /// The project-owned set is exactly what was reviewed. A new aggregate that
    /// hangs off a project must be added here deliberately, which is the moment
    /// to notice it needs the composite key too.
    /// </summary>
    [Fact]
    public void The_project_owned_root_set_is_exactly_what_was_reviewed()
    {
        ProjectOwnedTypes(BuildModel())
            .Select(e => e.ClrType.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBe(ProjectOwnedRoots);
    }

    /// <summary>
    /// <c>DesignProject</c> must expose the pair as a key, or nothing can
    /// reference it. Named separately because losing the alternate key would
    /// make every composite foreign key above impossible to declare.
    /// </summary>
    [Fact]
    public void The_project_exposes_its_tenant_pair_as_a_key()
    {
        var project = BuildModel().GetEntityTypes()
            .Single(e => e.ClrType.Name == "DesignProject");

        project.GetKeys()
            .Any(k => k.Properties.Select(p => p.Name).SequenceEqual(["OrganizationId", "Id"]))
            .ShouldBeTrue("app.projects must expose (organization_id, id) as a key");
    }

    /// <summary>
    /// A filter that ignored the scope — <c>x => true</c>, or one keyed on a
    /// constant — would satisfy every test above and protect nothing. This
    /// asserts each filter actually reads the context's scope members.
    /// </summary>
    [Fact]
    public void Each_organization_filter_reads_the_contexts_scope()
    {
        var model = BuildModel();

        foreach (var entityType in model.GetEntityTypes().Where(IsFiltered))
        {
            foreach (var filter in entityType.GetDeclaredQueryFilters())
            {
                var expression = filter.Expression?.ToString() ?? string.Empty;

                // String.Contains rather than ShouldContain: Shouldly resolves the
                // latter to its IEnumerable<char> overload for a string receiver.
                expression.Contains(
                    nameof(AppDbContext.ScopedOrganizationId), StringComparison.Ordinal)
                    .ShouldBeTrue(
                        $"{entityType.ClrType.Name}'s query filter must compare against the " +
                        $"request's organization scope. Filter was: {expression}");

                expression.Contains(nameof(AppDbContext.IsSystemScope), StringComparison.Ordinal)
                    .ShouldBeTrue(
                        $"{entityType.ClrType.Name}'s query filter must allow the explicit system " +
                        $"scope the worker runs in. Filter was: {expression}");
            }
        }
    }

    /// <summary>
    /// The exemption list is asserted by value. Growing it is the cheapest
    /// possible way to defeat every test above, so it cannot be done without
    /// editing this.
    /// </summary>
    [Fact]
    public void The_exemption_list_is_exactly_what_was_reviewed()
    {
        UnscopedByDesign.Keys.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBe(
            [
                "ApplicationRole",
                "ApplicationUser",
                "DataProtectionKey",
                "IdentityRoleClaim`1",
                "IdentityUserClaim`1",
                "IdentityUserLogin`1",
                "IdentityUserRole`1",
                "IdentityUserToken`1",
                "Organization",
                "OrganizationMembership",
            ]);

        // Every exemption states a reason. An unexplained one is how a tenant
        // table gets added to this list during a hurried fix.
        UnscopedByDesign.Values.ShouldAllBe(reason => reason.Length > 10);
    }
}
