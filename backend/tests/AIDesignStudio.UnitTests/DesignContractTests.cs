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

    private DesignContract NewContract() => DesignContract.Create(Guid.CreateVersion7(), _clock);

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

    [Fact]
    public void Create_requires_a_project()
    {
        Should.Throw<DomainException>(() => DesignContract.Create(Guid.Empty, _clock))
            .Code.ShouldBe("contract.project_required");
    }
}
