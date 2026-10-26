using AIDesignStudio.Domain.Projects;
using AIDesignStudio.SharedKernel;
using Shouldly;

namespace AIDesignStudio.UnitTests;

public sealed class DesignProjectTests
{
    private readonly TestClock _clock = new();

    [Fact]
    public void Create_requires_an_organization()
    {
        Should.Throw<DomainException>(() =>
                DesignProject.Create(Guid.Empty, "Acme", ProjectKind.NewProduct, _clock))
            .Code.ShouldBe("project.organization_required");
    }

    [Fact]
    public void Create_requires_a_name()
    {
        Should.Throw<DomainException>(() =>
                DesignProject.Create(Guid.CreateVersion7(), "  ", ProjectKind.NewProduct, _clock))
            .Code.ShouldBe("project.name_required");
    }

    [Theory]
    [InlineData(ProjectKind.NewProduct)]
    [InlineData(ProjectKind.Redesign)]
    public void Create_accepts_each_documented_workflow_kind(ProjectKind kind)
    {
        DesignProject.Create(Guid.CreateVersion7(), "Acme", kind, _clock).Kind.ShouldBe(kind);
    }

    /// <summary>
    /// Kind is immutable and selects the workflow, so an out-of-range value would
    /// be permanently wrong and leave later gates unable to choose a behaviour.
    /// Enums are stored as text, and a cast like <c>(ProjectKind)999</c> would
    /// otherwise persist unchallenged.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(999)]
    [InlineData(-1)]
    public void Create_rejects_a_kind_outside_the_documented_workflows(int raw)
    {
        Should.Throw<DomainException>(() =>
                DesignProject.Create(Guid.CreateVersion7(), "Acme", (ProjectKind)raw, _clock))
            .Code.ShouldBe("project.invalid_kind");
    }

    [Fact]
    public void Create_trims_the_name_and_records_creation_time()
    {
        var project = DesignProject.Create(
            Guid.CreateVersion7(), "  Acme Storefront  ", ProjectKind.Redesign, _clock);

        project.Name.ShouldBe("Acme Storefront");
        project.CreatedAt.ShouldBe(_clock.UtcNow);
        project.UpdatedAt.ShouldBeNull();
    }

    [Fact]
    public void Rename_requires_a_name_and_stamps_the_update()
    {
        var project = DesignProject.Create(
            Guid.CreateVersion7(), "Acme", ProjectKind.NewProduct, _clock);

        Should.Throw<DomainException>(() => project.Rename("  ", _clock))
            .Code.ShouldBe("project.name_required");

        _clock.Advance(TimeSpan.FromMinutes(5));
        project.Rename("  Acme Renamed  ", _clock);

        project.Name.ShouldBe("Acme Renamed");
        project.UpdatedAt.ShouldBe(_clock.UtcNow);
    }

    /// <summary>
    /// There is deliberately no API to change the workflow kind: the two
    /// workflows have different required inputs and different completion
    /// criteria, so switching would invalidate gate state.
    /// </summary>
    [Fact]
    public void There_is_no_public_way_to_change_the_workflow_kind()
    {
        typeof(DesignProject)
            .GetProperty(nameof(DesignProject.Kind))!
            .SetMethod!.IsPublic.ShouldBeFalse();

        typeof(DesignProject)
            .GetMethods()
            .Where(m => !m.IsSpecialName)
            .Where(m => m.Name.Contains("Kind", StringComparison.Ordinal))
            .ShouldBeEmpty("no method should offer to change the workflow kind");
    }
}
