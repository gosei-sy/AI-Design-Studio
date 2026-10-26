using AIDesignStudio.SharedKernel;

namespace AIDesignStudio.Domain.Screens;

/// <summary>
/// How a screen version's artifact came into the system. Tracked because it
/// determines what the artifact can be trusted to contain: an imported
/// screenshot is a raster image plus metadata, not an editable design tree.
/// See docs/06-spikes/SPIKE-03-screenshot-import.md.
/// </summary>
public enum ScreenOrigin
{
    /// <summary>Produced by an AI provider from a prompt.</summary>
    ProviderGenerated = 1,

    /// <summary>Uploaded raster screenshot. Carries no structural fidelity.</summary>
    ImportedScreenshot = 2,

    /// <summary>DOM/HTML snapshot captured from a running application.</summary>
    CapturedMarkup = 3,

    /// <summary>Authored by hand inside the studio.</summary>
    Authored = 4,
}

public enum ReviewState
{
    Draft = 1,
    InReview = 2,
    Approved = 3,
    Rejected = 4,
    Superseded = 5,
}

/// <summary>
/// Aggregate root for a single screen in a project, holding its version history.
/// A screen is identified within a project by <see cref="Key"/> (a stable slug or
/// route) so that redesign work can be matched against the existing application.
/// </summary>
public sealed class Screen : AggregateRoot
{
    private readonly List<ScreenVersion> _versions = [];

    private Screen() { } // EF Core

    public Guid ProjectId { get; private set; }
    public string Key { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;

    /// <summary>Version history, exposed as a non-castable wrapper. See DesignContract.Versions.</summary>
    public IReadOnlyList<ScreenVersion> Versions => _versions.AsReadOnly();

    public ScreenVersion? ApprovedVersion =>
        _versions.SingleOrDefault(v => v.ReviewState == ReviewState.Approved);

    public static Screen Create(Guid projectId, string key, string title, IClock clock)
    {
        if (projectId == Guid.Empty)
        {
            throw new DomainException("screen.project_required", "A screen must belong to a project.");
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            throw new DomainException("screen.key_required", "A screen must have a stable key.");
        }

        return new Screen
        {
            ProjectId = projectId,
            Key = key.Trim(),
            Title = string.IsNullOrWhiteSpace(title) ? key.Trim() : title.Trim(),
            CreatedAt = clock.UtcNow,
        };
    }

    /// <summary>
    /// Adds a version produced against a specific approved contract version.
    /// <paramref name="contractVersionId"/> and <paramref name="contractPayloadHash"/>
    /// are recorded so review can tell whether a screen is still being judged
    /// against the contract that is currently in force.
    /// </summary>
    public ScreenVersion AddVersion(
        ScreenOrigin origin,
        Guid contractVersionId,
        string contractPayloadHash,
        string? providerId,
        string? providerArtifactId,
        IClock clock)
    {
        if (contractVersionId == Guid.Empty)
        {
            throw new DomainException(
                "screen.contract_version_required",
                "A screen version must record the contract version it was produced against.");
        }

        var version = ScreenVersion.Create(
            screenId: Id,
            versionNumber: _versions.Count + 1,
            origin: origin,
            contractVersionId: contractVersionId,
            contractPayloadHash: contractPayloadHash,
            providerId: providerId,
            providerArtifactId: providerArtifactId,
            clock: clock);

        _versions.Add(version);
        Touch(clock);
        return version;
    }

    /// <summary>
    /// Approves a version against the contract version currently in force, and
    /// supersedes any previously approved version.
    ///
    /// Rejects approval when the version was produced against a different contract
    /// payload than the one now approved — the central guard that stops stale
    /// provider output from being promoted after the contract has moved on.
    ///
    /// Every precondition is checked *before* anything is mutated. An earlier
    /// version of this method superseded the current approval first and validated
    /// the target second, so a rejected command (wrong state, blank approver) left
    /// the aggregate with no approved version at all. A guard that throws and
    /// mutates is worse than no guard.
    /// Covered by ScreenApprovalTests.A_rejected_approval_leaves_an_existing_approval_intact.
    /// </summary>
    public void ApproveVersion(
        Guid versionId,
        Guid currentContractVersionId,
        string currentContractPayloadHash,
        string approvedBy,
        IClock clock)
    {
        var version = _versions.SingleOrDefault(v => v.Id == versionId)
            ?? throw new DomainException("screen.version_not_found", "The screen version does not exist.");

        if (version.ContractVersionId != currentContractVersionId ||
            !string.Equals(version.ContractPayloadHash, currentContractPayloadHash, StringComparison.Ordinal))
        {
            throw new DomainException(
                "screen.contract_drift",
                "This screen version was produced against a superseded design contract. " +
                "Regenerate it against the approved contract before approving.");
        }

        // Validate the full transition before touching any state.
        version.EnsureApprovable(approvedBy);

        ApprovedVersion?.Supersede();
        version.Approve(approvedBy, clock);
        Touch(clock);
    }
}

public sealed class ScreenVersion : Entity
{
    private ScreenVersion() { } // EF Core

    public Guid ScreenId { get; private set; }
    public int VersionNumber { get; private set; }
    public ScreenOrigin Origin { get; private set; }
    public ReviewState ReviewState { get; private set; }

    /// <summary>The contract version this artifact was produced against.</summary>
    public Guid ContractVersionId { get; private set; }

    /// <summary>Contract payload hash at production time; compared at approval to detect drift.</summary>
    public string ContractPayloadHash { get; private set; } = string.Empty;

    /// <summary>Provider that produced the artifact, e.g. <c>google-stitch</c>. Null when authored or imported.</summary>
    public string? ProviderId { get; private set; }

    /// <summary>The provider's own identifier for the artifact, for traceability back to its canvas.</summary>
    public string? ProviderArtifactId { get; private set; }

    public string? ApprovedBy { get; private set; }
    public DateTimeOffset? ApprovedAt { get; private set; }

    /// <summary>Reviewer feedback, retained so the author can act on it.</summary>
    public string? RejectionReason { get; private set; }
    public string? RejectedBy { get; private set; }
    public DateTimeOffset? RejectedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    internal static ScreenVersion Create(
        Guid screenId,
        int versionNumber,
        ScreenOrigin origin,
        Guid contractVersionId,
        string contractPayloadHash,
        string? providerId,
        string? providerArtifactId,
        IClock clock) => new()
        {
            ScreenId = screenId,
            VersionNumber = versionNumber,
            Origin = origin,
            ReviewState = ReviewState.Draft,
            ContractVersionId = contractVersionId,
            ContractPayloadHash = contractPayloadHash,
            ProviderId = providerId,
            ProviderArtifactId = providerArtifactId,
            CreatedAt = clock.UtcNow,
        };

    public void SubmitForReview()
    {
        if (ReviewState != ReviewState.Draft && ReviewState != ReviewState.Rejected)
        {
            throw new DomainException(
                "screen.not_submittable",
                $"A version in state {ReviewState} cannot be submitted for review.");
        }

        ReviewState = ReviewState.InReview;
    }

    /// <summary>
    /// Rejects a version under review, retaining the feedback.
    ///
    /// The reason, reviewer and timestamp are persisted rather than validated and
    /// discarded: the author needs the feedback to revise the screen, and a review
    /// record that cannot say why the state changed is ceremony rather than
    /// traceability.
    /// </summary>
    public void Reject(string reason, string rejectedBy, IClock clock)
    {
        if (ReviewState != ReviewState.InReview)
        {
            throw new DomainException("screen.not_in_review", "Only a version under review can be rejected.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("screen.rejection_reason_required", "A rejection must state a reason.");
        }

        if (string.IsNullOrWhiteSpace(rejectedBy))
        {
            throw new DomainException("screen.rejector_required", "A reviewer identity is required.");
        }

        ReviewState = ReviewState.Rejected;
        RejectionReason = reason.Trim();
        RejectedBy = rejectedBy.Trim();
        RejectedAt = clock.UtcNow;
    }

    /// <summary>
    /// Validates every precondition of approval without changing anything, so the
    /// aggregate can check before it mutates. See <see cref="Screen.ApproveVersion"/>.
    /// </summary>
    internal void EnsureApprovable(string approvedBy)
    {
        if (ReviewState != ReviewState.InReview)
        {
            throw new DomainException(
                "screen.not_in_review",
                $"Only a version under review can be approved; this one is {ReviewState}.");
        }

        if (string.IsNullOrWhiteSpace(approvedBy))
        {
            throw new DomainException("screen.approver_required", "An approver identity is required.");
        }
    }

    internal void Approve(string approvedBy, IClock clock)
    {
        EnsureApprovable(approvedBy);

        ReviewState = ReviewState.Approved;
        ApprovedBy = approvedBy.Trim();
        ApprovedAt = clock.UtcNow;
    }

    internal void Supersede() => ReviewState = ReviewState.Superseded;
}
