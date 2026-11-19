using AIDesignStudio.SharedKernel;

namespace AIDesignStudio.Domain.Projects;

/// <summary>
/// Which of the two product workflows a project follows. Chosen at creation and
/// immutable thereafter: the two workflows have different required inputs and
/// different completion criteria, so switching would invalidate gate state.
/// </summary>
public enum ProjectKind
{
    /// <summary>Brief -> UX architecture -> design system -> screens -> handoff.</summary>
    NewProduct = 1,

    /// <summary>Screenshots + feature inventory -> redesign without functional loss.</summary>
    Redesign = 2,
}

/// <summary>
/// Aggregate root of the Projects module. Owns tenancy and workflow kind; it does
/// not own contracts, screens, or assets — those are separate aggregates that
/// reference a project by id only.
/// </summary>
public sealed class DesignProject : AggregateRoot
{
    private DesignProject() { } // EF Core

    public Guid OrganizationId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public ProjectKind Kind { get; private set; }

    /// <summary>
    /// This project's tenancy, as the single value its children are created
    /// from (W3-RR-001).
    ///
    /// <para>Every aggregate that hangs off a project takes a
    /// <see cref="ProjectOwnership"/> rather than two loose ids, so the ordinary
    /// way to create one — <c>Screen.Create(project.Ownership, …)</c> — cannot
    /// pair this project with another organization. The database enforces the
    /// same thing with <c>fk_&lt;table&gt;_project_tenant</c>; this is what makes
    /// the correct call the convenient one.</para>
    ///
    /// <para>Computed, not stored. Mapped as ignored in <c>AppDbContext</c>.</para>
    /// </summary>
    public ProjectOwnership Ownership => ProjectOwnership.Of(OrganizationId, Id);

    /// <summary>
    /// The longest name, after trimming. Matches the <c>varchar(200)</c> column:
    /// without the rule a longer name reached the database and failed there as an
    /// unclassified error rather than a coded one (M01-04).
    /// </summary>
    public const int MaxNameLength = 200;

    public static DesignProject Create(Guid organizationId, string name, ProjectKind kind, IClock clock)
    {
        if (organizationId == Guid.Empty)
        {
            throw new DomainException("project.organization_required", "A project must belong to an organization.");
        }

        var trimmed = ValidName(name);

        // Guarding the enum matters because Kind is immutable and drives workflow
        // gating. A cast like (ProjectKind)999 would otherwise persist — enums are
        // stored as text — and leave every later gate unable to select a valid
        // behaviour for a project that can never be corrected.
        if (!Enum.IsDefined(kind))
        {
            throw new DomainException(
                "project.invalid_kind",
                $"'{(int)kind}' is not a valid project kind. Use NewProduct or Redesign.");
        }

        return new DesignProject
        {
            OrganizationId = organizationId,
            Name = trimmed,
            Kind = kind,
            CreatedAt = clock.UtcNow,
        };
    }

    public void Rename(string name, IClock clock)
    {
        Name = ValidName(name);
        Touch(clock);
    }

    private static string ValidName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("project.name_required", "A project must have a name.");
        }

        var trimmed = name.Trim();

        if (trimmed.Length > MaxNameLength)
        {
            throw new DomainException(
                "project.name_too_long",
                $"A project name may be at most {MaxNameLength} characters; got {trimmed.Length}.");
        }

        return trimmed;
    }
}
