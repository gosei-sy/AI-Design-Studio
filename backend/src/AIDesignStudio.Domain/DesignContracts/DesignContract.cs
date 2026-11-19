using System.Security.Cryptography;
using System.Text;
using AIDesignStudio.SharedKernel;

namespace AIDesignStudio.Domain.DesignContracts;

public enum ContractVersionStatus
{
    Draft = 1,
    Approved = 2,
    Superseded = 3,
}

/// <summary>
/// Aggregate root for the Design Contract: the normative description of what the
/// product must contain (information architecture, flows, screen obligations,
/// accessibility and content rules).
///
/// This is the system's source of truth. AI provider output is evidence that a
/// contract was satisfied; it is never itself the contract. See
/// docs/02-architecture/decisions/ADR-0002-design-contract-is-source-of-truth.md.
///
/// Versioning rules enforced here:
///   1. Version numbers are dense and monotonic, starting at 1.
///   2. Only a Draft version is mutable. Approved and Superseded payloads are frozen.
///   3. At most one Approved version exists at any time.
///   4. Approving a version supersedes the previously approved version.
///   5. A payload hash is recorded at approval so downstream artifacts can prove
///      which exact contract bytes they were produced against.
/// </summary>
public sealed class DesignContract : AggregateRoot
{
    private readonly List<DesignContractVersion> _versions = [];

    private DesignContract() { } // EF Core

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

    /// <summary>
    /// Version history, exposed as a non-castable wrapper.
    ///
    /// Returning <c>_versions</c> directly would hand out the live
    /// <see cref="List{T}"/>: a caller could cast the IReadOnlyList back and
    /// Add, Remove or Clear it, removing an approved version without going
    /// through any guarded operation. That would defeat the immutability this
    /// aggregate exists to provide (ADR-0002).
    /// </summary>
    public IReadOnlyList<DesignContractVersion> Versions => _versions.AsReadOnly();

    public static DesignContract Create(ProjectOwnership owner, IClock clock)
    {
        var (organizationId, projectId) = (owner.OrganizationId, owner.ProjectId);

        if (organizationId == Guid.Empty)
        {
            throw new DomainException(
                "contract.organization_required", "A design contract must belong to an organization.");
        }

        if (projectId == Guid.Empty)
        {
            throw new DomainException("contract.project_required", "A design contract must belong to a project.");
        }

        return new DesignContract
        {
            OrganizationId = organizationId,
            ProjectId = projectId,
            CreatedAt = clock.UtcNow,
        };
    }

    public DesignContractVersion? ApprovedVersion =>
        _versions.SingleOrDefault(v => v.Status == ContractVersionStatus.Approved);

    public DesignContractVersion? DraftVersion =>
        _versions.SingleOrDefault(v => v.Status == ContractVersionStatus.Draft);

    /// <summary>
    /// Starts a new draft version. Only one draft may be open at a time so that
    /// reviewers always have a single candidate to approve.
    /// </summary>
    public DesignContractVersion StartDraft(string payloadJson, IClock clock)
    {
        if (DraftVersion is not null)
        {
            throw new DomainException(
                "contract.draft_already_open",
                "A draft version is already open. Edit or discard it before starting another.");
        }

        var version = DesignContractVersion.CreateDraft(
            contractId: Id,
            versionNumber: _versions.Count + 1,
            payloadJson: payloadJson,
            clock: clock);

        _versions.Add(version);
        Touch(clock);
        return version;
    }

    /// <summary>
    /// Approves the open draft and supersedes the previously approved version.
    /// This is the only transition that changes what the rest of the system treats
    /// as authoritative, so it is deliberately the only way to produce an
    /// Approved version.
    /// </summary>
    public DesignContractVersion ApproveDraft(string approvedBy, IClock clock)
    {
        var draft = DraftVersion
            ?? throw new DomainException("contract.no_draft", "There is no draft version to approve.");

        if (string.IsNullOrWhiteSpace(approvedBy))
        {
            throw new DomainException("contract.approver_required", "An approver identity is required.");
        }

        ApprovedVersion?.Supersede(clock);
        draft.Approve(approvedBy, clock);
        Touch(clock);
        return draft;
    }

    public void DiscardDraft(IClock clock)
    {
        var draft = DraftVersion
            ?? throw new DomainException("contract.no_draft", "There is no draft version to discard.");

        _versions.Remove(draft);
        Touch(clock);
    }
}

/// <summary>
/// An immutable-once-approved snapshot of the design contract. Part of the
/// <see cref="DesignContract"/> aggregate; never loaded or mutated independently.
/// </summary>
public sealed class DesignContractVersion : Entity
{
    private DesignContractVersion() { } // EF Core

    public Guid ContractId { get; private set; }
    public int VersionNumber { get; private set; }
    public ContractVersionStatus Status { get; private set; }

    /// <summary>
    /// The contract document, validated against the shared JSON Schema in
    /// <c>packages/contracts</c> before it reaches the domain.
    /// </summary>
    public string PayloadJson { get; private set; } = string.Empty;

    /// <summary>SHA-256 of <see cref="PayloadJson"/>, fixed at approval time.</summary>
    public string? PayloadHash { get; private set; }

    public string? ApprovedBy { get; private set; }
    public DateTimeOffset? ApprovedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    internal static DesignContractVersion CreateDraft(
        Guid contractId, int versionNumber, string payloadJson, IClock clock)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            throw new DomainException("contract.payload_required", "A contract version must have a payload.");
        }

        return new DesignContractVersion
        {
            ContractId = contractId,
            VersionNumber = versionNumber,
            Status = ContractVersionStatus.Draft,
            PayloadJson = payloadJson,
            CreatedAt = clock.UtcNow,
        };
    }

    /// <summary>
    /// Replaces the draft payload. Rejected on non-draft versions: an approved
    /// contract that could still change would make every downstream approval
    /// meaningless.
    /// </summary>
    public void UpdatePayload(string payloadJson, IClock clock)
    {
        if (Status != ContractVersionStatus.Draft)
        {
            throw new DomainException(
                "contract.version_immutable",
                $"Version {VersionNumber} is {Status} and cannot be modified. Start a new draft instead.");
        }

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            throw new DomainException("contract.payload_required", "A contract version must have a payload.");
        }

        PayloadJson = payloadJson;
        _ = clock;
    }

    internal void Approve(string approvedBy, IClock clock)
    {
        if (Status != ContractVersionStatus.Draft)
        {
            throw new DomainException(
                "contract.not_draft",
                $"Only a draft version can be approved; version {VersionNumber} is {Status}.");
        }

        Status = ContractVersionStatus.Approved;
        ApprovedBy = approvedBy;
        ApprovedAt = clock.UtcNow;
        PayloadHash = ComputeHash(PayloadJson);
    }

    internal void Supersede(IClock clock)
    {
        if (Status != ContractVersionStatus.Approved)
        {
            throw new DomainException(
                "contract.not_approved",
                $"Only an approved version can be superseded; version {VersionNumber} is {Status}.");
        }

        Status = ContractVersionStatus.Superseded;
        _ = clock;
    }

    public static string ComputeHash(string payloadJson) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson)));
}
