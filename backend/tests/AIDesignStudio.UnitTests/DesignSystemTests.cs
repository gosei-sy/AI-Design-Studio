using AIDesignStudio.Domain.DesignSystems;
using AIDesignStudio.SharedKernel;
using Shouldly;

namespace AIDesignStudio.UnitTests;

/// <summary>
/// Covers the Design System lifecycle.
///
/// PRD AC-3.1 requires that design system versioning and approval behave
/// identically to the Design Contract. M00 originally claimed that parity as
/// verified while no such suite existed — and the aggregate was in fact missing
/// a discard operation and an approval hash. This suite is the evidence, and it
/// deliberately mirrors <see cref="DesignContractTests"/> case for case so a
/// divergence shows up as a missing test rather than a silent gap.
/// </summary>
public sealed class DesignSystemTests
{
    private readonly TestClock _clock = new();

    /// <summary>
    /// A complete, schema-valid design system, kept in step with
    /// `packages/contracts/schemas/design-system.schema.json` by hand — it
    /// carried the pre-M00-R3-002 <c>color.primary</c> shape until R4-003.
    ///
    /// The shorter blobs further down (<c>{"schemaVersion":1,"name":"Revised"}</c>
    /// and friends) are deliberately not valid design systems. Those tests are
    /// about version immutability and content hashing, where the tokens are an
    /// opaque string whose only relevant property is whether it changed. The
    /// domain does not validate tokens at M00; schema enforcement arrives at the
    /// API boundary in M01, which is where a fixture like this one becomes
    /// load-bearing.
    /// </summary>
    private const string Tokens = """
        {"schemaVersion":1,"name":"Acme","typography":{"bodyFont":"Inter","headlineFont":"Manrope"},
         "color":{"roles":{"primary":"#3366ff"}},"shape":{"radiusBase":"8px"}}
        """;

    private DesignSystem NewSystem() =>
        DesignSystem.Create(ProjectOwnership.Of(Guid.CreateVersion7(), Guid.CreateVersion7()), "Acme Design System", _clock);

    // -----------------------------------------------------------------------
    // Creation
    // -----------------------------------------------------------------------

    [Fact]
    public void Create_requires_a_project()
    {
        Should.Throw<DomainException>(() => DesignSystem.Create(ProjectOwnership.Of(Guid.CreateVersion7(), Guid.Empty), "Acme", _clock))
            .Code.ShouldBe("design_system.project_required");
    }

    [Fact]
    public void Create_requires_a_name()
    {
        Should.Throw<DomainException>(() => DesignSystem.Create(ProjectOwnership.Of(Guid.CreateVersion7(), Guid.CreateVersion7()), "  ", _clock))
            .Code.ShouldBe("design_system.name_required");
    }

    [Fact]
    public void Create_trims_the_name()
    {
        DesignSystem.Create(ProjectOwnership.Of(Guid.CreateVersion7(), Guid.CreateVersion7()), "  Acme  ", _clock).Name.ShouldBe("Acme");
    }

    // -----------------------------------------------------------------------
    // Drafting — parity with DesignContractTests
    // -----------------------------------------------------------------------

    [Fact]
    public void StartDraft_assigns_version_numbers_from_one_and_increments()
    {
        var system = NewSystem();

        system.StartDraft(Tokens, _clock).VersionNumber.ShouldBe(1);
        system.ApproveDraft("alice", _clock);
        system.StartDraft(Tokens, _clock).VersionNumber.ShouldBe(2);
    }

    [Fact]
    public void StartDraft_rejects_a_second_concurrent_draft()
    {
        var system = NewSystem();
        system.StartDraft(Tokens, _clock);

        Should.Throw<DomainException>(() => system.StartDraft(Tokens, _clock))
            .Code.ShouldBe("design_system.draft_already_open");
    }

    [Fact]
    public void StartDraft_rejects_empty_tokens()
    {
        Should.Throw<DomainException>(() => NewSystem().StartDraft("   ", _clock))
            .Code.ShouldBe("design_system.tokens_required");
    }

    [Fact]
    public void A_draft_version_can_be_modified()
    {
        var draft = NewSystem().StartDraft(Tokens, _clock);

        draft.UpdateTokens("""{"schemaVersion":1,"name":"Revised"}""");

        draft.TokensJson.ShouldContain("Revised");
        draft.TokensHash.ShouldBeNull("a hash is only fixed at approval");
    }

    [Fact]
    public void UpdateTokens_rejects_an_empty_payload()
    {
        var draft = NewSystem().StartDraft(Tokens, _clock);

        Should.Throw<DomainException>(() => draft.UpdateTokens("  "))
            .Code.ShouldBe("design_system.tokens_required");
    }

    /// <summary>
    /// StartDraft's error tells callers to "edit or discard" the open draft. That
    /// instruction referred to an operation that did not exist until M00-CX-007.
    /// </summary>
    [Fact]
    public void DiscardDraft_removes_the_draft_and_frees_its_version_number()
    {
        var system = NewSystem();
        system.StartDraft(Tokens, _clock);

        system.DiscardDraft(_clock);

        system.Versions.ShouldBeEmpty();
        system.DraftVersion.ShouldBeNull();
        system.StartDraft(Tokens, _clock).VersionNumber.ShouldBe(1);
    }

    [Fact]
    public void DiscardDraft_fails_when_no_draft_is_open()
    {
        Should.Throw<DomainException>(() => NewSystem().DiscardDraft(_clock))
            .Code.ShouldBe("design_system.no_draft");
    }

    [Fact]
    public void DiscardDraft_does_not_touch_an_approved_version()
    {
        var system = NewSystem();
        system.StartDraft(Tokens, _clock);
        var approved = system.ApproveDraft("alice", _clock);

        system.StartDraft("""{"schemaVersion":1,"name":"Next"}""", _clock);
        system.DiscardDraft(_clock);

        system.ApprovedVersion.ShouldBe(approved);
        system.Versions.Count.ShouldBe(1);
    }

    // -----------------------------------------------------------------------
    // Approval
    // -----------------------------------------------------------------------

    [Fact]
    public void ApproveDraft_records_approver_timestamp_and_tokens_hash()
    {
        var system = NewSystem();
        system.StartDraft(Tokens, _clock);

        var approved = system.ApproveDraft("  alice@example.com  ", _clock);

        approved.Status.ShouldBe(DesignSystemStatus.Approved);
        approved.ApprovedBy.ShouldBe("alice@example.com");
        approved.ApprovedAt.ShouldBe(_clock.UtcNow);
        approved.TokensHash.ShouldBe(DesignSystemVersion.ComputeHash(Tokens));
    }

    [Fact]
    public void ApproveDraft_requires_an_approver_identity()
    {
        var system = NewSystem();
        system.StartDraft(Tokens, _clock);

        Should.Throw<DomainException>(() => system.ApproveDraft("  ", _clock))
            .Code.ShouldBe("design_system.approver_required");
    }

    [Fact]
    public void ApproveDraft_fails_when_no_draft_is_open()
    {
        Should.Throw<DomainException>(() => NewSystem().ApproveDraft("alice", _clock))
            .Code.ShouldBe("design_system.no_draft");
    }

    [Fact]
    public void Approving_a_new_version_supersedes_the_previous_approved_version()
    {
        var system = NewSystem();

        system.StartDraft(Tokens, _clock);
        var first = system.ApproveDraft("alice", _clock);

        system.StartDraft("""{"schemaVersion":1,"name":"v2"}""", _clock);
        var second = system.ApproveDraft("bob", _clock);

        first.Status.ShouldBe(DesignSystemStatus.Superseded);
        second.Status.ShouldBe(DesignSystemStatus.Approved);
        system.Versions.Count(v => v.Status == DesignSystemStatus.Approved).ShouldBe(1);
        system.ApprovedVersion.ShouldBe(second);
    }

    // -----------------------------------------------------------------------
    // Immutability
    // -----------------------------------------------------------------------

    [Fact]
    public void An_approved_version_cannot_be_modified()
    {
        var system = NewSystem();
        system.StartDraft(Tokens, _clock);
        var approved = system.ApproveDraft("alice", _clock);

        Should.Throw<DomainException>(() => approved.UpdateTokens("""{"tampered":true}"""))
            .Code.ShouldBe("design_system.version_immutable");

        approved.TokensJson.ShouldBe(Tokens);
    }

    [Fact]
    public void A_superseded_version_cannot_be_modified()
    {
        var system = NewSystem();
        system.StartDraft(Tokens, _clock);
        var first = system.ApproveDraft("alice", _clock);
        system.StartDraft("""{"schemaVersion":1,"name":"v2"}""", _clock);
        system.ApproveDraft("bob", _clock);

        first.Status.ShouldBe(DesignSystemStatus.Superseded);

        Should.Throw<DomainException>(() => first.UpdateTokens("""{"tampered":true}"""))
            .Code.ShouldBe("design_system.version_immutable");
    }

    [Fact]
    public void A_version_cannot_be_removed_from_the_history_from_outside()
    {
        var system = NewSystem();
        system.StartDraft(Tokens, _clock);
        var approved = system.ApproveDraft("alice", _clock);

        MutationAttempt.TryClear(system.Versions);

        system.Versions.Count.ShouldBe(1);
        system.ApprovedVersion.ShouldBe(approved);
    }

    // -----------------------------------------------------------------------
    // Hashing
    // -----------------------------------------------------------------------

    [Fact]
    public void ComputeHash_is_stable_and_differs_for_different_token_sets()
    {
        var a = DesignSystemVersion.ComputeHash(Tokens);

        a.ShouldBe(DesignSystemVersion.ComputeHash(Tokens));
        a.Length.ShouldBe(64);
        a.ShouldNotBe(DesignSystemVersion.ComputeHash("""{"schemaVersion":1,"name":"Other"}"""));
    }

    /// <summary>
    /// The hash must cover the bytes that were approved, not the bytes the draft
    /// started with — otherwise an edit before approval would not be reflected.
    /// </summary>
    [Fact]
    public void The_approval_hash_covers_the_final_draft_content()
    {
        const string edited = """{"schemaVersion":1,"name":"Edited before approval"}""";

        var system = NewSystem();
        var draft = system.StartDraft(Tokens, _clock);
        draft.UpdateTokens(edited);

        system.ApproveDraft("alice", _clock)
            .TokensHash.ShouldBe(DesignSystemVersion.ComputeHash(edited));
    }
}
