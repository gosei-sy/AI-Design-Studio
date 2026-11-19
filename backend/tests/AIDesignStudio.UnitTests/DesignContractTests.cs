using AIDesignStudio.Domain.DesignContracts;
using AIDesignStudio.SharedKernel;
using Shouldly;

namespace AIDesignStudio.UnitTests;

/// <summary>
/// Covers the versioning invariants of the Design Contract. These are the
/// system's load-bearing rules: if an approved contract can change, every
/// downstream approval becomes meaningless.
/// </summary>
public sealed class DesignContractTests
{
    private readonly TestClock _clock = new();

    private DesignContract NewContract() => DesignContract.Create(ProjectOwnership.Of(Guid.CreateVersion7(), Guid.CreateVersion7()), _clock);

    [Fact]
    public void StartDraft_assigns_version_numbers_from_one_and_increments()
    {
        var contract = NewContract();

        contract.StartDraft("""{"v":1}""", _clock).VersionNumber.ShouldBe(1);
        contract.ApproveDraft("alice", _clock);
        contract.StartDraft("""{"v":2}""", _clock).VersionNumber.ShouldBe(2);
    }

    [Fact]
    public void StartDraft_rejects_a_second_concurrent_draft()
    {
        var contract = NewContract();
        contract.StartDraft("""{"v":1}""", _clock);

        var ex = Should.Throw<DomainException>(() => contract.StartDraft("""{"v":2}""", _clock));
        ex.Code.ShouldBe("contract.draft_already_open");
    }

    [Fact]
    public void StartDraft_rejects_an_empty_payload()
    {
        var contract = NewContract();

        Should.Throw<DomainException>(() => contract.StartDraft("   ", _clock))
            .Code.ShouldBe("contract.payload_required");
    }

    [Fact]
    public void ApproveDraft_records_approver_timestamp_and_payload_hash()
    {
        var contract = NewContract();
        const string payload = """{"screens":["home"]}""";
        contract.StartDraft(payload, _clock);

        var approved = contract.ApproveDraft("alice@example.com", _clock);

        approved.Status.ShouldBe(ContractVersionStatus.Approved);
        approved.ApprovedBy.ShouldBe("alice@example.com");
        approved.ApprovedAt.ShouldBe(_clock.UtcNow);
        approved.PayloadHash.ShouldBe(DesignContractVersion.ComputeHash(payload));
    }

    [Fact]
    public void ApproveDraft_requires_an_approver_identity()
    {
        var contract = NewContract();
        contract.StartDraft("""{"v":1}""", _clock);

        Should.Throw<DomainException>(() => contract.ApproveDraft("  ", _clock))
            .Code.ShouldBe("contract.approver_required");
    }

    [Fact]
    public void ApproveDraft_fails_when_no_draft_is_open()
    {
        Should.Throw<DomainException>(() => NewContract().ApproveDraft("alice", _clock))
            .Code.ShouldBe("contract.no_draft");
    }

    /// <summary>
    /// The central invariant: exactly one version is authoritative at a time.
    /// </summary>
    [Fact]
    public void Approving_a_new_version_supersedes_the_previous_approved_version()
    {
        var contract = NewContract();

        contract.StartDraft("""{"v":1}""", _clock);
        var first = contract.ApproveDraft("alice", _clock);

        contract.StartDraft("""{"v":2}""", _clock);
        var second = contract.ApproveDraft("bob", _clock);

        first.Status.ShouldBe(ContractVersionStatus.Superseded);
        second.Status.ShouldBe(ContractVersionStatus.Approved);
        contract.Versions.Count(v => v.Status == ContractVersionStatus.Approved).ShouldBe(1);
        contract.ApprovedVersion.ShouldBe(second);
    }

    [Fact]
    public void An_approved_version_cannot_be_modified()
    {
        var contract = NewContract();
        contract.StartDraft("""{"v":1}""", _clock);
        var approved = contract.ApproveDraft("alice", _clock);

        Should.Throw<DomainException>(() => approved.UpdatePayload("""{"tampered":true}""", _clock))
            .Code.ShouldBe("contract.version_immutable");
    }

    [Fact]
    public void A_superseded_version_cannot_be_modified()
    {
        var contract = NewContract();
        contract.StartDraft("""{"v":1}""", _clock);
        var first = contract.ApproveDraft("alice", _clock);
        contract.StartDraft("""{"v":2}""", _clock);
        contract.ApproveDraft("bob", _clock);

        first.Status.ShouldBe(ContractVersionStatus.Superseded);

        Should.Throw<DomainException>(() => first.UpdatePayload("""{"tampered":true}""", _clock))
            .Code.ShouldBe("contract.version_immutable");
    }

    [Fact]
    public void A_draft_version_can_be_modified()
    {
        var contract = NewContract();
        var draft = contract.StartDraft("""{"v":1}""", _clock);

        draft.UpdatePayload("""{"v":"1-revised"}""", _clock);

        draft.PayloadJson.ShouldBe("""{"v":"1-revised"}""");
        draft.PayloadHash.ShouldBeNull("a hash is only fixed at approval");
    }

    [Fact]
    public void DiscardDraft_removes_the_draft_and_frees_its_version_number()
    {
        var contract = NewContract();
        contract.StartDraft("""{"v":1}""", _clock);

        contract.DiscardDraft(_clock);

        contract.Versions.ShouldBeEmpty();
        contract.StartDraft("""{"v":"new"}""", _clock).VersionNumber.ShouldBe(1);
    }

    [Fact]
    public void ComputeHash_is_stable_and_differs_for_different_payloads()
    {
        var a = DesignContractVersion.ComputeHash("""{"v":1}""");

        a.ShouldBe(DesignContractVersion.ComputeHash("""{"v":1}"""));
        a.Length.ShouldBe(64);
        a.ShouldNotBe(DesignContractVersion.ComputeHash("""{"v":2}"""));
    }

    /// <summary>
    /// Returning the live backing list would let a caller cast the
    /// IReadOnlyList back and remove an approved version, defeating the
    /// immutability this aggregate exists to provide (M00-CX-004).
    /// </summary>
    [Fact]
    public void An_approved_version_cannot_be_removed_from_the_history_from_outside()
    {
        var contract = NewContract();
        contract.StartDraft("""{"v":1}""", _clock);
        var approved = contract.ApproveDraft("alice", _clock);

        MutationAttempt.TryClear(contract.Versions);

        contract.Versions.Count.ShouldBe(1);
        contract.ApprovedVersion.ShouldBe(approved);
    }

    [Fact]
    public void The_version_collection_is_not_castable_to_a_mutable_list()
    {
        var contract = NewContract();
        contract.StartDraft("""{"v":1}""", _clock);

        (contract.Versions as List<DesignContractVersion>).ShouldBeNull();
    }

    // --- UpdateDraft (M01-05) ------------------------------------------------
    // "Edit the contract" as the API means it: there is one editable thing, the
    // open draft. The aggregate decides what an edit means when there is none,
    // so the HTTP layer cannot pick a version to modify.

    [Fact]
    public void UpdateDraft_replaces_the_open_drafts_payload_and_stamps_the_contract()
    {
        var contract = NewContract();
        contract.StartDraft("""{"v":1}""", _clock);
        _clock.Advance(TimeSpan.FromMinutes(3));

        var draft = contract.UpdateDraft("""{"v":"edited"}""", _clock);

        draft.ShouldBe(contract.DraftVersion);
        draft.PayloadJson.ShouldBe("""{"v":"edited"}""");
        draft.VersionNumber.ShouldBe(1);
        contract.UpdatedAt.ShouldBe(_clock.UtcNow);
    }

    /// <summary>
    /// The case the studio must explain: the contract is approved and someone
    /// tries to change it. That is <c>contract.version_immutable</c>, raised by
    /// the version's own guard, and the approved payload is untouched.
    /// </summary>
    [Fact]
    public void UpdateDraft_with_no_draft_open_refuses_to_touch_the_approved_version()
    {
        var contract = NewContract();
        contract.StartDraft("""{"v":1}""", _clock);
        var approved = contract.ApproveDraft("alice", _clock);

        Should.Throw<DomainException>(() => contract.UpdateDraft("""{"v":"tampered"}""", _clock))
            .Code.ShouldBe("contract.version_immutable");

        approved.PayloadJson.ShouldBe("""{"v":1}""");
        approved.PayloadHash.ShouldBe(DesignContractVersion.ComputeHash("""{"v":1}"""));
    }

    [Fact]
    public void UpdateDraft_on_a_contract_with_no_versions_reports_that_there_is_no_draft()
    {
        Should.Throw<DomainException>(() => NewContract().UpdateDraft("""{"v":1}""", _clock))
            .Code.ShouldBe("contract.no_draft");
    }

    [Fact]
    public void UpdateDraft_after_a_discard_reports_the_approved_version_as_immutable()
    {
        var contract = NewContract();
        contract.StartDraft("""{"v":1}""", _clock);
        contract.ApproveDraft("alice", _clock);
        contract.StartDraft("""{"v":2}""", _clock);
        contract.DiscardDraft(_clock);

        Should.Throw<DomainException>(() => contract.UpdateDraft("""{"v":3}""", _clock))
            .Code.ShouldBe("contract.version_immutable");
    }

    [Fact]
    public void UpdateDraft_rejects_an_empty_payload_and_keeps_the_draft()
    {
        var contract = NewContract();
        contract.StartDraft("""{"v":1}""", _clock);

        Should.Throw<DomainException>(() => contract.UpdateDraft("  ", _clock))
            .Code.ShouldBe("contract.payload_required");

        contract.DraftVersion!.PayloadJson.ShouldBe("""{"v":1}""");
    }

    [Fact]
    public void Create_requires_a_project()
    {
        Should.Throw<DomainException>(() => DesignContract.Create(ProjectOwnership.Of(Guid.CreateVersion7(), Guid.Empty), _clock))
            .Code.ShouldBe("contract.project_required");
    }
}
