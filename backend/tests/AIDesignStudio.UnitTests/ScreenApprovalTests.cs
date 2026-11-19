using AIDesignStudio.Domain.Screens;
using AIDesignStudio.SharedKernel;
using Shouldly;

namespace AIDesignStudio.UnitTests;

/// <summary>
/// Covers the guard that ties provider output back to the contract it was
/// produced against. Without it, a screen generated against an old contract
/// could be approved after the contract changed, which is exactly how a design
/// system silently drifts away from its own source of truth.
/// </summary>
public sealed class ScreenApprovalTests
{
    private readonly TestClock _clock = new();
    private readonly Guid _contractVersionId = Guid.CreateVersion7();
    private const string ContractHash = "abc123";

    private Screen NewScreen() =>
        Screen.Create(ProjectOwnership.Of(Guid.CreateVersion7(), Guid.CreateVersion7()), "/checkout", "Checkout", _clock);

    private ScreenVersion AddVersion(Screen screen, Guid? contractVersionId = null, string? hash = null) =>
        screen.AddVersion(
            ScreenOrigin.ProviderGenerated,
            contractVersionId ?? _contractVersionId,
            hash ?? ContractHash,
            providerId: "google-stitch",
            providerArtifactId: "screen-42",
            clock: _clock);

    [Fact]
    public void Create_requires_a_stable_key()
    {
        Should.Throw<DomainException>(() => Screen.Create(ProjectOwnership.Of(Guid.CreateVersion7(), Guid.CreateVersion7()), "  ", "T", _clock))
            .Code.ShouldBe("screen.key_required");
    }

    [Fact]
    public void Create_defaults_the_title_to_the_key()
    {
        Screen.Create(ProjectOwnership.Of(Guid.CreateVersion7(), Guid.CreateVersion7()), "/checkout", "  ", _clock).Title.ShouldBe("/checkout");
    }

    [Fact]
    public void AddVersion_records_provenance_and_the_contract_it_targets()
    {
        var version = AddVersion(NewScreen());

        version.VersionNumber.ShouldBe(1);
        version.ReviewState.ShouldBe(ReviewState.Draft);
        version.Origin.ShouldBe(ScreenOrigin.ProviderGenerated);
        version.ProviderId.ShouldBe("google-stitch");
        version.ProviderArtifactId.ShouldBe("screen-42");
        version.ContractVersionId.ShouldBe(_contractVersionId);
    }

    [Fact]
    public void AddVersion_requires_a_contract_version()
    {
        var screen = NewScreen();

        Should.Throw<DomainException>(() => screen.AddVersion(
                ScreenOrigin.Authored, Guid.Empty, ContractHash, null, null, _clock))
            .Code.ShouldBe("screen.contract_version_required");
    }

    [Fact]
    public void A_version_must_pass_through_review_before_approval()
    {
        var screen = NewScreen();
        var version = AddVersion(screen);

        Should.Throw<DomainException>(() => screen.ApproveVersion(
                version.Id, _contractVersionId, ContractHash, "alice", _clock))
            .Code.ShouldBe("screen.not_in_review");
    }

    [Fact]
    public void Approval_succeeds_when_the_version_matches_the_contract_in_force()
    {
        var screen = NewScreen();
        var version = AddVersion(screen);
        version.SubmitForReview();

        screen.ApproveVersion(version.Id, _contractVersionId, ContractHash, "alice", _clock);

        version.ReviewState.ShouldBe(ReviewState.Approved);
        version.ApprovedBy.ShouldBe("alice");
        screen.ApprovedVersion.ShouldBe(version);
    }

    /// <summary>
    /// Contract drift: the version targets a contract version that is no longer
    /// the one in force.
    /// </summary>
    [Fact]
    public void Approval_is_blocked_when_the_contract_version_has_moved_on()
    {
        var screen = NewScreen();
        var version = AddVersion(screen);
        version.SubmitForReview();

        var newContractVersionId = Guid.CreateVersion7();

        var ex = Should.Throw<DomainException>(() => screen.ApproveVersion(
            version.Id, newContractVersionId, "def456", "alice", _clock));

        ex.Code.ShouldBe("screen.contract_drift");
        version.ReviewState.ShouldBe(ReviewState.InReview, "a blocked approval must not change state");
    }

    /// <summary>
    /// The subtle case: the same contract version id, but different payload bytes.
    /// Matching on id alone would let edited contract content through.
    /// </summary>
    [Fact]
    public void Approval_is_blocked_when_the_contract_payload_hash_differs()
    {
        var screen = NewScreen();
        var version = AddVersion(screen);
        version.SubmitForReview();

        Should.Throw<DomainException>(() => screen.ApproveVersion(
                version.Id, _contractVersionId, "a-different-hash", "alice", _clock))
            .Code.ShouldBe("screen.contract_drift");
    }

    [Fact]
    public void Approving_a_new_version_supersedes_the_previous_approved_version()
    {
        var screen = NewScreen();

        var first = AddVersion(screen);
        first.SubmitForReview();
        screen.ApproveVersion(first.Id, _contractVersionId, ContractHash, "alice", _clock);

        var second = AddVersion(screen);
        second.SubmitForReview();
        screen.ApproveVersion(second.Id, _contractVersionId, ContractHash, "bob", _clock);

        first.ReviewState.ShouldBe(ReviewState.Superseded);
        second.ReviewState.ShouldBe(ReviewState.Approved);
        screen.Versions.Count(v => v.ReviewState == ReviewState.Approved).ShouldBe(1);
    }

    [Fact]
    public void A_rejected_version_can_be_resubmitted_and_requires_a_reason()
    {
        var version = AddVersion(NewScreen());
        version.SubmitForReview();

        Should.Throw<DomainException>(() => version.Reject("  ", "bob", _clock))
            .Code.ShouldBe("screen.rejection_reason_required");

        version.Reject("Contrast ratio below AA on the primary button.", "bob", _clock);
        version.ReviewState.ShouldBe(ReviewState.Rejected);

        version.SubmitForReview();
        version.ReviewState.ShouldBe(ReviewState.InReview);
    }

    // -----------------------------------------------------------------------
    // A rejected approval must change nothing (M00-CX-005)
    // -----------------------------------------------------------------------

    /// <summary>
    /// The earlier implementation superseded the current approval before
    /// validating the target, so an invalid command left the aggregate with no
    /// approved version at all. Each case below has an existing approval, which
    /// is what the original tests lacked.
    /// </summary>
    [Theory]
    [InlineData("draft")]
    [InlineData("rejected")]
    [InlineData("already-approved")]
    [InlineData("blank-approver")]
    public void A_rejected_approval_leaves_an_existing_approval_intact(string scenario)
    {
        var screen = NewScreen();

        var approved = AddVersion(screen);
        approved.SubmitForReview();
        screen.ApproveVersion(approved.Id, _contractVersionId, ContractHash, "alice", _clock);

        var (targetId, approver) = scenario switch
        {
            // Never submitted for review.
            "draft" => (AddVersion(screen).Id, "bob"),

            "rejected" => (RejectedVersion(screen).Id, "bob"),

            // Re-approving the version that is already approved.
            "already-approved" => (approved.Id, "bob"),

            // In review, but no approver identity.
            "blank-approver" => (SubmittedVersion(screen).Id, "   "),

            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        Should.Throw<DomainException>(() => screen.ApproveVersion(
            targetId, _contractVersionId, ContractHash, approver, _clock));

        approved.ReviewState.ShouldBe(
            ReviewState.Approved, "a rejected approval must not supersede the existing approval");

        screen.ApprovedVersion.ShouldBe(approved);
        screen.Versions.Count(v => v.ReviewState == ReviewState.Approved).ShouldBe(1);
    }

    [Fact]
    public void Contract_drift_leaves_an_existing_approval_intact()
    {
        var screen = NewScreen();

        var approved = AddVersion(screen);
        approved.SubmitForReview();
        screen.ApproveVersion(approved.Id, _contractVersionId, ContractHash, "alice", _clock);

        var stale = AddVersion(screen, Guid.CreateVersion7(), "stale-hash");
        stale.SubmitForReview();

        Should.Throw<DomainException>(() => screen.ApproveVersion(
                stale.Id, _contractVersionId, ContractHash, "bob", _clock))
            .Code.ShouldBe("screen.contract_drift");

        approved.ReviewState.ShouldBe(ReviewState.Approved);
        stale.ReviewState.ShouldBe(ReviewState.InReview);
    }

    private ScreenVersion SubmittedVersion(Screen screen)
    {
        var version = AddVersion(screen);
        version.SubmitForReview();
        return version;
    }

    private ScreenVersion RejectedVersion(Screen screen)
    {
        var version = SubmittedVersion(screen);
        version.Reject("Contrast below AA.", "carol", _clock);
        return version;
    }

    // -----------------------------------------------------------------------
    // Rejection feedback is retained (M00-CX-008)
    // -----------------------------------------------------------------------

    [Fact]
    public void A_rejection_retains_its_reason_reviewer_and_timestamp()
    {
        var version = SubmittedVersion(NewScreen());

        version.Reject("  Contrast ratio below AA on the primary button.  ", "  carol  ", _clock);

        version.ReviewState.ShouldBe(ReviewState.Rejected);
        version.RejectionReason.ShouldBe("Contrast ratio below AA on the primary button.");
        version.RejectedBy.ShouldBe("carol");
        version.RejectedAt.ShouldBe(_clock.UtcNow);
    }

    [Fact]
    public void A_rejection_requires_a_reviewer_identity()
    {
        var version = SubmittedVersion(NewScreen());

        Should.Throw<DomainException>(() => version.Reject("Too cramped.", "  ", _clock))
            .Code.ShouldBe("screen.rejector_required");

        version.ReviewState.ShouldBe(ReviewState.InReview, "a refused rejection must change nothing");
        version.RejectionReason.ShouldBeNull();
    }

    // -----------------------------------------------------------------------
    // Version history cannot be mutated from outside (M00-CX-004)
    // -----------------------------------------------------------------------

    [Fact]
    public void An_approved_version_cannot_be_removed_from_the_history()
    {
        var screen = NewScreen();
        var approved = SubmittedVersion(screen);
        screen.ApproveVersion(approved.Id, _contractVersionId, ContractHash, "alice", _clock);

        MutationAttempt.TryClear(screen.Versions);

        screen.Versions.Count.ShouldBe(1);
        screen.ApprovedVersion.ShouldBe(approved);
    }

    [Fact]
    public void ApproveVersion_rejects_an_unknown_version_id()
    {
        var screen = NewScreen();
        AddVersion(screen);

        Should.Throw<DomainException>(() => screen.ApproveVersion(
                Guid.CreateVersion7(), _contractVersionId, ContractHash, "alice", _clock))
            .Code.ShouldBe("screen.version_not_found");
    }

    [Fact]
    public void Approval_requires_an_approver_identity()
    {
        var screen = NewScreen();
        var version = AddVersion(screen);
        version.SubmitForReview();

        Should.Throw<DomainException>(() => screen.ApproveVersion(
                version.Id, _contractVersionId, ContractHash, "  ", _clock))
            .Code.ShouldBe("screen.approver_required");
    }
}
