using AIDesignStudio.Domain.Redesign;
using AIDesignStudio.SharedKernel;
using Shouldly;

namespace AIDesignStudio.UnitTests;

/// <summary>
/// Covers the redesign workflow's core promise: a redesign cannot lose
/// functionality by accident. Dropping a feature must be a recorded decision,
/// never silence.
/// </summary>
public sealed class FeatureInventoryTests
{
    private readonly TestClock _clock = new();

    private FeatureInventory NewInventory() => FeatureInventory.Create(ProjectOwnership.Of(Guid.CreateVersion7(), Guid.CreateVersion7()), _clock);

    [Fact]
    public void A_newly_catalogued_feature_starts_unaddressed_and_blocks_handoff()
    {
        var inventory = NewInventory();
        inventory.AddItem("export-csv", "Export the report as CSV", _clock);

        inventory.Items.Single().Coverage.ShouldBe(CoverageState.Unaddressed);
        inventory.BlockingItems.Count.ShouldBe(1);

        Should.Throw<DomainException>(inventory.EnsureReadyForHandoff)
            .Code.ShouldBe("inventory.uncovered_features");
    }

    [Fact]
    public void AddItem_rejects_a_duplicate_key_case_insensitively()
    {
        var inventory = NewInventory();
        inventory.AddItem("export-csv", "Export CSV", _clock);

        Should.Throw<DomainException>(() => inventory.AddItem("Export-CSV", "Dup", _clock))
            .Code.ShouldBe("inventory.duplicate_key");
    }

    [Fact]
    public void AddItem_requires_a_key()
    {
        Should.Throw<DomainException>(() => NewInventory().AddItem("  ", "no key", _clock))
            .Code.ShouldBe("inventory.key_required");
    }

    [Fact]
    public void A_covered_feature_no_longer_blocks_handoff()
    {
        var inventory = NewInventory();
        var item = inventory.AddItem("export-csv", "Export CSV", _clock);
        var screenId = Guid.CreateVersion7();

        item.MarkCovered([screenId]);

        item.Coverage.ShouldBe(CoverageState.Covered);
        item.CoveringScreenIds.ShouldBe([screenId]);
        inventory.BlockingItems.ShouldBeEmpty();

        Should.NotThrow(inventory.EnsureReadyForHandoff);
    }

    /// <summary>
    /// "Covered" must point at something. Otherwise the gate can be satisfied by
    /// asserting coverage that does not exist.
    /// </summary>
    [Fact]
    public void MarkCovered_requires_at_least_one_real_screen()
    {
        var item = NewInventory().AddItem("export-csv", "Export CSV", _clock);

        Should.Throw<DomainException>(() => item.MarkCovered([]))
            .Code.ShouldBe("inventory.covering_screen_required");

        Should.Throw<DomainException>(() => item.MarkCovered([Guid.Empty]))
            .Code.ShouldBe("inventory.covering_screen_required");
    }

    [Fact]
    public void A_waived_feature_no_longer_blocks_handoff_but_records_the_decision()
    {
        var inventory = NewInventory();
        var item = inventory.AddItem("legacy-fax", "Fax the report", _clock);

        item.Waive("Zero usage in the last 12 months.", "product-owner@example.com");

        item.Coverage.ShouldBe(CoverageState.Waived);
        item.WaiverReason.ShouldBe("Zero usage in the last 12 months.");
        item.WaivedBy.ShouldBe("product-owner@example.com");
        inventory.BlockingItems.ShouldBeEmpty();

        Should.NotThrow(inventory.EnsureReadyForHandoff);
    }

    [Fact]
    public void Waive_requires_a_reason_and_an_accountable_owner()
    {
        var item = NewInventory().AddItem("legacy-fax", "Fax", _clock);

        Should.Throw<DomainException>(() => item.Waive("   ", "owner"))
            .Code.ShouldBe("inventory.waiver_reason_required");

        Should.Throw<DomainException>(() => item.Waive("Unused", "  "))
            .Code.ShouldBe("inventory.waiver_owner_required");

        item.Coverage.ShouldBe(CoverageState.Unaddressed, "a rejected waiver must not change coverage");
    }

    [Fact]
    public void Covering_a_previously_waived_feature_clears_the_waiver()
    {
        var item = NewInventory().AddItem("legacy-fax", "Fax", _clock);
        item.Waive("Unused", "owner");

        item.MarkCovered([Guid.CreateVersion7()]);

        item.Coverage.ShouldBe(CoverageState.Covered);
        item.WaiverReason.ShouldBeNull();
        item.WaivedBy.ShouldBeNull();
    }

    [Fact]
    public void Waiving_a_previously_covered_feature_clears_its_covering_screens()
    {
        var item = NewInventory().AddItem("export-csv", "Export CSV", _clock);
        item.MarkCovered([Guid.CreateVersion7()]);

        item.Waive("Replaced by the API", "owner");

        item.CoveringScreenIds.ShouldBeEmpty();
    }

    [Fact]
    public void The_handoff_gate_names_the_blocking_features()
    {
        var inventory = NewInventory();
        inventory.AddItem("export-csv", "Export CSV", _clock);
        inventory.AddItem("bulk-edit", "Bulk edit", _clock);

        var ex = Should.Throw<DomainException>(inventory.EnsureReadyForHandoff);

        ex.Message.ShouldContain("export-csv");
        ex.Message.ShouldContain("bulk-edit");
    }

    // -----------------------------------------------------------------------
    // Invariant bypass through mutable collections (M00-CX-004)
    // -----------------------------------------------------------------------

    /// <summary>
    /// The exploit this guard exists for: clear the covering screens after
    /// MarkCovered validated them, and the item stays Covered with nothing
    /// covering it. BlockingItems only blocks on Unaddressed, so the handoff gate
    /// would then pass — the no-functional-loss guarantee defeated with one call.
    /// </summary>
    [Fact]
    public void Covering_screens_cannot_be_cleared_from_outside_the_aggregate()
    {
        var inventory = NewInventory();
        var item = inventory.AddItem("export-csv", "Export CSV", _clock);
        item.MarkCovered([Guid.CreateVersion7()]);

        MutationAttempt.TryClear(item.CoveringScreenIds);

        item.CoveringScreenIds.ShouldNotBeEmpty("coverage must survive an external mutation attempt");
        Should.NotThrow(inventory.EnsureReadyForHandoff);
    }

    [Fact]
    public void A_catalogued_feature_cannot_be_removed_from_outside_the_aggregate()
    {
        var inventory = NewInventory();
        inventory.AddItem("export-csv", "Export CSV", _clock);

        MutationAttempt.TryClear(inventory.Items);

        inventory.Items.Count.ShouldBe(1, "dropping an item would shrink what the gate checks");
        Should.Throw<DomainException>(inventory.EnsureReadyForHandoff)
            .Code.ShouldBe("inventory.uncovered_features");
    }

    [Fact]
    public void The_covering_screen_collection_is_not_castable_to_a_mutable_list()
    {
        var item = NewInventory().AddItem("export-csv", "Export CSV", _clock);
        item.MarkCovered([Guid.CreateVersion7()]);

        (item.CoveringScreenIds as List<Guid>).ShouldBeNull();
        item.CoveringScreenIds.ShouldBeAssignableTo<IReadOnlyList<Guid>>();
    }

    [Fact]
    public void An_empty_inventory_does_not_block_handoff()
    {
        Should.NotThrow(NewInventory().EnsureReadyForHandoff);
    }
}
