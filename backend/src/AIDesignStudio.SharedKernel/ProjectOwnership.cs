namespace AIDesignStudio.SharedKernel;

/// <summary>
/// A project and the organization that owns it, carried as **one value**.
///
/// <para><b>Why this exists (W3-RR-001).</b> The four project-owned aggregates
/// used to be created from two independent <see cref="Guid"/> parameters —
/// <c>organizationId</c> and <c>projectId</c> — which a caller could fill with
/// an organization and a project belonging to someone else. The re-review proved
/// PostgreSQL accepted exactly that. The database now refuses it with a composite
/// foreign key; this type is the other half of the correction, so the natural way
/// to write the call is also the correct one:</para>
///
/// <code>
/// DesignContract.Create(project.Ownership, clock);   // one value, from a loaded project
/// </code>
///
/// <para><b>What this type does not do.</b> It is not unforgeable: anything can
/// call <see cref="Of"/> with two unrelated ids. The guarantee lives in the
/// database — <c>fk_&lt;table&gt;_project_tenant</c> — and this is a shape that
/// makes the mistake conspicuous rather than idiomatic. Claiming more would be
/// the same overstatement as "the keys cannot drift", which is the sentence that
/// produced the defect.</para>
///
/// <para>In SharedKernel because every domain module needs it, and a type in one
/// module would be a cross-module reference the architecture tests forbid.</para>
/// </summary>
public readonly record struct ProjectOwnership
{
    private ProjectOwnership(Guid organizationId, Guid projectId)
    {
        OrganizationId = organizationId;
        ProjectId = projectId;
    }

    /// <summary>The organization that owns the project, and therefore its children.</summary>
    public Guid OrganizationId { get; }

    public Guid ProjectId { get; }

    /// <summary>
    /// Pairs the two ids.
    ///
    /// <para>Validation is deliberately left to the calling aggregate: each
    /// module raises its own stable error code — <c>contract.project_required</c>,
    /// <c>screen.organization_required</c> and the rest — and those codes are a
    /// published contract (API_CONTRACTS §1). Centralising the check here would
    /// have replaced them with one generic code, which is a breaking change for
    /// no gain.</para>
    ///
    /// <para>A <c>default(ProjectOwnership)</c> therefore carries two empty ids
    /// and is caught by exactly those per-module checks.</para>
    /// </summary>
    public static ProjectOwnership Of(Guid organizationId, Guid projectId) =>
        new(organizationId, projectId);

    public override string ToString() => $"organization {OrganizationId} / project {ProjectId}";
}
