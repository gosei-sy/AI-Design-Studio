using System.Security.Cryptography;
using System.Text;
using AIDesignStudio.SharedKernel;

namespace AIDesignStudio.Domain.DesignSystems;

public enum DesignSystemStatus
{
    Draft = 1,
    Approved = 2,
    Superseded = 3,
}

/// <summary>
/// Aggregate root for the approved Design System: the token set and component
/// rules that screens must conform to.
///
/// Versioned on the same Draft -> Approved -> Superseded model as
/// <see cref="DesignContracts.DesignContract"/>, and for the same reason: an
/// approval is only meaningful if what was approved cannot subsequently change.
///
/// The token set here is intentionally richer than any provider's native theme
/// model. Provider themes are produced by projecting this (lossily) at request
/// time — never by importing theirs. See
/// docs/02-architecture/decisions/ADR-0004-provider-theme-projection-is-lossy.md.
/// </summary>
public sealed class DesignSystem : AggregateRoot
{
    private readonly List<DesignSystemVersion> _versions = [];

    private DesignSystem() { } // EF Core

    /// <summary>
    /// The owning organization. **Denormalized from the project on purpose**
    /// (W3-001).
    ///
    /// <para>Tenancy is transitive — this aggregate belongs to a project, and the
    /// project belongs to an organization — but a transitive key cannot be a
    /// global query filter without a correlated subquery on every read of every
    /// table. Carrying the tenant key directly is what makes the filter uniform
    /// across the model, and it follows the shape <c>AssetReference</c> and
    /// <c>JobRecord</c> have had since M00: both store <c>OrganizationId</c>
    /// alongside <c>ProjectId</c>.</para>
    ///
    /// <para>Safe to denormalize because a project's organization is immutable —
    /// <c>DesignProject</c> exposes no way to move one between tenants — so the
    /// two values cannot drift.</para>
    /// </summary>
    public Guid OrganizationId { get; private set; }

    public Guid ProjectId { get; private set; }
    public string Name { get; private set; } = string.Empty;

    /// <summary>Version history, exposed as a non-castable wrapper. See DesignContract.Versions.</summary>
    public IReadOnlyList<DesignSystemVersion> Versions => _versions.AsReadOnly();

    public DesignSystemVersion? ApprovedVersion =>
        _versions.SingleOrDefault(v => v.Status == DesignSystemStatus.Approved);

    public DesignSystemVersion? DraftVersion =>
        _versions.SingleOrDefault(v => v.Status == DesignSystemStatus.Draft);

    public static DesignSystem Create(ProjectOwnership owner, string name, IClock clock)
    {
        var (organizationId, projectId) = (owner.OrganizationId, owner.ProjectId);

        if (organizationId == Guid.Empty)
        {
            throw new DomainException(
                "design_system.organization_required", "A design system must belong to an organization.");
        }

        if (projectId == Guid.Empty)
        {
            throw new DomainException("design_system.project_required", "A design system must belong to a project.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("design_system.name_required", "A design system must have a name.");
        }

        return new DesignSystem
        {
            OrganizationId = organizationId,
            ProjectId = projectId,
            Name = name.Trim(),
            CreatedAt = clock.UtcNow,
        };
    }

    public DesignSystemVersion StartDraft(string tokensJson, IClock clock)
    {
        if (DraftVersion is not null)
        {
            throw new DomainException(
                "design_system.draft_already_open",
                "A draft version is already open. Edit or discard it before starting another.");
        }

        if (string.IsNullOrWhiteSpace(tokensJson))
        {
            throw new DomainException("design_system.tokens_required", "A design system version must have tokens.");
        }

        var version = DesignSystemVersion.CreateDraft(Id, _versions.Count + 1, tokensJson, clock);
        _versions.Add(version);
        Touch(clock);
        return version;
    }

    public DesignSystemVersion ApproveDraft(string approvedBy, IClock clock)
    {
        var draft = DraftVersion
            ?? throw new DomainException("design_system.no_draft", "There is no draft version to approve.");

        if (string.IsNullOrWhiteSpace(approvedBy))
        {
            throw new DomainException("design_system.approver_required", "An approver identity is required.");
        }

        ApprovedVersion?.Supersede();
        draft.Approve(approvedBy, clock);
        Touch(clock);
        return draft;
    }

    /// <summary>
    /// Discards the open draft, freeing its version number.
    ///
    /// Required for parity with <see cref="DesignContracts.DesignContract"/> per
    /// PRD AC-3.1, and because <see cref="StartDraft"/>'s own error tells callers
    /// to "edit or discard" the open draft — an instruction that previously
    /// referred to an operation that did not exist.
    /// </summary>
    public void DiscardDraft(IClock clock)
    {
        var draft = DraftVersion
            ?? throw new DomainException("design_system.no_draft", "There is no draft version to discard.");

        _versions.Remove(draft);
        Touch(clock);
    }
}

public sealed class DesignSystemVersion : Entity
{
    private DesignSystemVersion() { } // EF Core

    public Guid DesignSystemId { get; private set; }
    public int VersionNumber { get; private set; }
    public DesignSystemStatus Status { get; private set; }

    /// <summary>
    /// Token set, validated against <c>packages/contracts/schemas/design-system.schema.json</c>.
    /// </summary>
    public string TokensJson { get; private set; } = string.Empty;

    /// <summary>
    /// SHA-256 of <see cref="TokensJson"/>, fixed at approval.
    ///
    /// Present for the same reason as the contract's payload hash: so a downstream
    /// artifact can prove which exact token bytes it was produced against, and so
    /// tampering with an approved row is detectable. Required for AC-3.1 parity.
    /// </summary>
    public string? TokensHash { get; private set; }

    public string? ApprovedBy { get; private set; }
    public DateTimeOffset? ApprovedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    internal static DesignSystemVersion CreateDraft(
        Guid designSystemId, int versionNumber, string tokensJson, IClock clock) => new()
        {
            DesignSystemId = designSystemId,
            VersionNumber = versionNumber,
            Status = DesignSystemStatus.Draft,
            TokensJson = tokensJson,
            CreatedAt = clock.UtcNow,
        };

    public void UpdateTokens(string tokensJson)
    {
        if (Status != DesignSystemStatus.Draft)
        {
            throw new DomainException(
                "design_system.version_immutable",
                $"Version {VersionNumber} is {Status} and cannot be modified. Start a new draft instead.");
        }

        if (string.IsNullOrWhiteSpace(tokensJson))
        {
            throw new DomainException("design_system.tokens_required", "A design system version must have tokens.");
        }

        TokensJson = tokensJson;
    }

    internal void Approve(string approvedBy, IClock clock)
    {
        if (Status != DesignSystemStatus.Draft)
        {
            throw new DomainException(
                "design_system.not_draft",
                $"Only a draft version can be approved; version {VersionNumber} is {Status}.");
        }

        Status = DesignSystemStatus.Approved;
        ApprovedBy = approvedBy.Trim();
        ApprovedAt = clock.UtcNow;
        TokensHash = ComputeHash(TokensJson);
    }

    internal void Supersede() => Status = DesignSystemStatus.Superseded;

    public static string ComputeHash(string tokensJson) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(tokensJson)));
}
