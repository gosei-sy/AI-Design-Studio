using System.Collections;
using System.Reflection;
using AIDesignStudio.Application.Providers;
using AIDesignStudio.Domain.DesignContracts;
using AIDesignStudio.Domain.DesignSystems;
using AIDesignStudio.Domain.Projects;
using AIDesignStudio.Domain.Redesign;
using AIDesignStudio.Domain.Screens;
using AIDesignStudio.Infrastructure.Persistence;
using AIDesignStudio.SharedKernel;
using NetArchTest.Rules;
using Shouldly;

namespace AIDesignStudio.ArchitectureTests;

/// <summary>
/// Executable enforcement of the layering and module boundaries documented in
/// docs/02-architecture/SYSTEM_ARCHITECTURE.md.
///
/// A modular monolith stays modular only if the boundaries are checked. Written
/// down, they erode within a few milestones; tested, a violation fails the build.
/// </summary>
public sealed class ModuleBoundaryTests
{
    private static readonly Assembly SharedKernel = typeof(Entity).Assembly;
    private static readonly Assembly Domain = typeof(DesignProject).Assembly;
    private static readonly Assembly Application = typeof(IDesignProvider).Assembly;
    private static readonly Assembly Infrastructure = typeof(AppDbContext).Assembly;

    private const string SharedKernelNs = "AIDesignStudio.SharedKernel";
    private const string DomainNs = "AIDesignStudio.Domain";
    private const string ApplicationNs = "AIDesignStudio.Application";
    private const string InfrastructureNs = "AIDesignStudio.Infrastructure";
    private const string ApiNs = "AIDesignStudio.Api";
    private const string WorkerNs = "AIDesignStudio.Worker";

    // The operator provisioning tool is a host like the others, and nothing
    // inward may depend on it. Added when M01-03 introduced it (ADR-0009):
    // omitting it from these lists would have left a new executable free to be
    // referenced from the domain.
    private const string ProvisioningNs = "AIDesignStudio.Provisioning";

    private static void ShouldHaveNoViolations(NetArchTest.Rules.TestResult result)
    {
        var failing = result.FailingTypeNames ?? [];
        result.IsSuccessful.ShouldBeTrue($"Boundary violations: {string.Join(", ", failing)}");
    }

    // -----------------------------------------------------------------------
    // Layering: dependencies point inward only.
    // -----------------------------------------------------------------------

    [Fact]
    public void SharedKernel_depends_on_nothing_else_in_the_solution() =>
        ShouldHaveNoViolations(Types.InAssembly(SharedKernel)
            .ShouldNot()
            .HaveDependencyOnAny(
                DomainNs, ApplicationNs, InfrastructureNs, ApiNs, WorkerNs, ProvisioningNs)
            .GetResult());

    [Fact]
    public void Domain_does_not_depend_on_outer_layers() =>
        ShouldHaveNoViolations(Types.InAssembly(Domain)
            .ShouldNot()
            .HaveDependencyOnAny(
                ApplicationNs, InfrastructureNs, ApiNs, WorkerNs, ProvisioningNs)
            .GetResult());

    [Fact]
    public void Application_does_not_depend_on_infrastructure_or_hosts() =>
        ShouldHaveNoViolations(Types.InAssembly(Application)
            .ShouldNot()
            .HaveDependencyOnAny(InfrastructureNs, ApiNs, WorkerNs, ProvisioningNs)
            .GetResult());

    // -----------------------------------------------------------------------
    // The domain stays free of technology choices.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Entity Framework must not reach the domain. If it does, persistence
    /// concerns start shaping invariants, and the domain stops being testable
    /// without a database.
    /// </summary>
    [Fact]
    public void Domain_has_no_persistence_dependency() =>
        ShouldHaveNoViolations(Types.InAssembly(Domain)
            .ShouldNot()
            .HaveDependencyOnAny("Microsoft.EntityFrameworkCore", "Npgsql", "Dapper", "System.Data")
            .GetResult());

    [Fact]
    public void Domain_and_Application_have_no_web_framework_dependency()
    {
        foreach (var assembly in new[] { Domain, Application })
        {
            ShouldHaveNoViolations(Types.InAssembly(assembly)
                .ShouldNot()
                .HaveDependencyOnAny("Microsoft.AspNetCore", "Microsoft.Azure", "Azure.Storage")
                .GetResult());
        }
    }

    [Fact]
    public void Application_has_no_persistence_dependency() =>
        ShouldHaveNoViolations(Types.InAssembly(Application)
            .ShouldNot()
            .HaveDependencyOnAny("Microsoft.EntityFrameworkCore", "Npgsql")
            .GetResult());

    // -----------------------------------------------------------------------
    // Provider isolation: the product's core principle, enforced.
    // -----------------------------------------------------------------------

    /// <summary>
    /// No provider's name may appear outside its adapter. The moment "Stitch"
    /// leaks into the domain or application layer, the Design Contract has stopped
    /// being provider-neutral and the product's central claim is false.
    /// See ADR-0002 and ADR-0003.
    /// </summary>
    [Theory]
    [InlineData("Stitch")]
    [InlineData("Gemini")]
    [InlineData("Figma")]
    public void No_provider_name_appears_in_the_domain_or_application_layer(string providerName)
    {
        foreach (var assembly in new[] { SharedKernel, Domain, Application })
        {
            var offenders = assembly.GetTypes()
                .Where(t => t.Name.Contains(providerName, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.FullName)
                .ToList();

            offenders.ShouldBeEmpty(
                $"'{providerName}' must only appear in Infrastructure adapters, but leaked into " +
                $"{assembly.GetName().Name}");
        }
    }

    /// <summary>
    /// Every provider adapter lives under the Providers folder in Infrastructure,
    /// so finding "everything that talks to an external AI service" is a directory
    /// listing rather than a search.
    /// </summary>
    [Fact]
    public void Every_provider_adapter_lives_in_the_infrastructure_providers_namespace()
    {
        var adapters = Infrastructure.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false })
            .Where(t => typeof(IDesignProvider).IsAssignableFrom(t))
            .ToList();

        adapters.ShouldNotBeEmpty("the solution should register at least one provider adapter");

        foreach (var adapter in adapters)
        {
            adapter.Namespace.ShouldNotBeNull();
            adapter.Namespace!.ShouldStartWith($"{InfrastructureNs}.Providers");
        }
    }

    /// <summary>
    /// A capability interface marks something a provider may or may not support,
    /// so it must be declared where the abstraction lives, not next to an adapter.
    /// </summary>
    [Fact]
    public void Capability_contracts_are_declared_in_the_application_layer()
    {
        var capabilities = Application.GetTypes()
            .Where(t => t.IsInterface && typeof(IProviderCapability).IsAssignableFrom(t))
            .Where(t => t != typeof(IProviderCapability))
            .ToList();

        capabilities.ShouldNotBeEmpty();

        Infrastructure.GetTypes()
            .Where(t => t.IsInterface && typeof(IProviderCapability).IsAssignableFrom(t))
            .ShouldBeEmpty("capability contracts belong to the Application layer, not to an adapter");
    }

    // -----------------------------------------------------------------------
    // Cross-module coupling inside the domain.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Domain modules may not reference each other's types. Cross-module links are
    /// by aggregate id only, which is what keeps a module replaceable and keeps a
    /// change in one module from rippling through the others.
    /// </summary>
    [Fact]
    public void Domain_modules_do_not_reference_each_others_types()
    {
        string[] modules =
        [
            "Projects", "DesignContracts", "DesignSystems", "Screens", "Redesign", "Assets", "Jobs",
            "Organizations",
        ];
        var violations = new List<string>();

        foreach (var module in modules)
        {
            var others = modules
                .Where(m => m != module)
                .Select(m => $"{DomainNs}.{m}")
                .ToArray();

            var result = Types.InAssembly(Domain)
                .That().ResideInNamespace($"{DomainNs}.{module}")
                .ShouldNot().HaveDependencyOnAny(others)
                .GetResult();

            if (!result.IsSuccessful)
            {
                violations.AddRange((result.FailingTypeNames ?? []).Select(t => $"{module}: {t}"));
            }
        }

        violations.ShouldBeEmpty();
    }

    // -----------------------------------------------------------------------
    // Domain modelling conventions that the invariants rely on.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Aggregate roots must be sealed. An inheritable aggregate can have its
    /// invariant checks overridden, which silently defeats them.
    /// </summary>
    [Fact]
    public void Aggregate_roots_are_sealed() =>
        ShouldHaveNoViolations(Types.InAssembly(Domain)
            .That().Inherit(typeof(AggregateRoot))
            .Should().BeSealed()
            .GetResult());

    /// <summary>
    /// Domain state must not be publicly settable. Every change goes through a
    /// method that can enforce the relevant invariant.
    /// </summary>
    [Fact]
    public void Domain_entities_expose_no_public_setters()
    {
        var offenders = Domain.GetTypes()
            .Where(t => typeof(Entity).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.SetMethod is { IsPublic: true })
                .Select(p => $"{t.Name}.{p.Name}"))
            .ToList();

        offenders.ShouldBeEmpty();
    }

    /// <summary>
    /// No domain type may hand out a mutable collection.
    ///
    /// The existing public-setter test did not catch this: a property typed
    /// <c>IReadOnlyList&lt;T&gt;</c> whose runtime value is a <c>List&lt;T&gt;</c>
    /// can be cast back and mutated, so a caller could remove an approved version
    /// or clear an item's covering screens without going through any guarded
    /// operation (M00-CX-004). Checking the declared type is not enough — this
    /// test instantiates each aggregate and inspects what is actually returned.
    /// </summary>
    [Fact]
    public void Domain_entities_never_hand_out_a_mutable_collection()
    {
        var clock = new SystemClock();

        // One populated instance per aggregate that owns a collection.
        var contract = DesignContract.Create(ProjectOwnership.Of(Guid.CreateVersion7(), Guid.CreateVersion7()), clock);
        contract.StartDraft("""{"v":1}""", clock);

        var designSystem = DesignSystem.Create(ProjectOwnership.Of(Guid.CreateVersion7(), Guid.CreateVersion7()), "Acme", clock);
        designSystem.StartDraft("""{"v":1}""", clock);

        var screen = Screen.Create(ProjectOwnership.Of(Guid.CreateVersion7(), Guid.CreateVersion7()), "home", "Home", clock);
        screen.AddVersion(ScreenOrigin.Authored, Guid.CreateVersion7(), "hash", null, null, clock);

        var inventory = FeatureInventory.Create(ProjectOwnership.Of(Guid.CreateVersion7(), Guid.CreateVersion7()), clock);
        var item = inventory.AddItem("export-csv", "Export CSV", clock);
        item.MarkCovered([Guid.CreateVersion7()]);

        object[] instances = [contract, designSystem, screen, inventory, item];
        var offenders = new List<string>();

        foreach (var instance in instances)
        {
            var properties = instance.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetIndexParameters().Length == 0)
                .Where(p => typeof(IEnumerable).IsAssignableFrom(p.PropertyType))
                .Where(p => p.PropertyType != typeof(string));

            foreach (var property in properties)
            {
                var value = property.GetValue(instance);

                if (value is null)
                {
                    continue;
                }

                // A genuinely read-only wrapper implements ICollection<T> with
                // IsReadOnly true, or does not implement it at all.
                var mutable = value.GetType()
                    .GetInterfaces()
                    .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICollection<>))
                    .Select(i => (bool)i.GetProperty("IsReadOnly")!.GetValue(value)!)
                    .Any(isReadOnly => !isReadOnly);

                if (mutable)
                {
                    offenders.Add(
                        $"{instance.GetType().Name}.{property.Name} returns a mutable " +
                        $"{value.GetType().Name}");
                }
            }
        }

        offenders.ShouldBeEmpty();
    }

    /// <summary>
    /// Time must arrive through <see cref="IClock"/>. A direct DateTime.UtcNow
    /// call makes lease expiry and retry backoff untestable.
    /// </summary>
    [Fact]
    public void Nothing_in_the_domain_or_application_layer_reads_the_system_clock_directly()
    {
        foreach (var assembly in new[] { Domain, Application })
        {
            var result = Types.InAssembly(assembly)
                .ShouldNot()
                .HaveDependencyOn("System.TimeProvider")
                .GetResult();

            ShouldHaveNoViolations(result);
        }

        // SystemClock is the single sanctioned reader of wall-clock time.
        typeof(SystemClock).Assembly.ShouldBe(SharedKernel);
    }
}
