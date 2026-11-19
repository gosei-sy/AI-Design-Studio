using AIDesignStudio.SharedKernel;

namespace AIDesignStudio.Domain.Redesign;

public enum CoverageState
{
    /// <summary>Catalogued from the existing application, not yet addressed by the redesign.</summary>
    Unaddressed = 1,

    /// <summary>Mapped to at least one screen in the redesign.</summary>
    Covered = 2,

    /// <summary>Deliberately dropped, with a recorded decision and owner.</summary>
    Waived = 3,
}

/// <summary>
/// Aggregate root of the Redesign module: the inventory of capabilities the
/// existing application provides today.
///
/// Its purpose is to make functional loss impossible to achieve by accident. A
/// redesign cannot reach handoff while any catalogued feature is still
/// <see cref="CoverageState.Unaddressed"/>; dropping a feature requires an
/// explicit, attributed waiver. Silence is never treated as a decision.
/// </summary>
public sealed class FeatureInventory : AggregateRoot
{
    private readonly List<FeatureInventoryItem> _items = [];

    private FeatureInventory() { } // EF Core

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
    /// Catalogued features, exposed as a non-castable wrapper so an item cannot
    /// be removed from the inventory outside a guarded operation — which would
    /// silently shrink what the handoff gate checks (ADR-0007).
    /// </summary>
    public IReadOnlyList<FeatureInventoryItem> Items => _items.AsReadOnly();

    public static FeatureInventory Create(ProjectOwnership owner, IClock clock)
    {
        var (organizationId, projectId) = (owner.OrganizationId, owner.ProjectId);

        if (organizationId == Guid.Empty)
        {
            throw new DomainException(
                "inventory.organization_required", "A feature inventory must belong to an organization.");
        }

        if (projectId == Guid.Empty)
        {
            throw new DomainException("inventory.project_required", "A feature inventory must belong to a project.");
        }

        return new FeatureInventory
        {
            OrganizationId = organizationId,
            ProjectId = projectId,
            CreatedAt = clock.UtcNow,
        };
    }

    public FeatureInventoryItem AddItem(string key, string description, IClock clock)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new DomainException("inventory.key_required", "A feature must have a stable key.");
        }

        var normalized = key.Trim();

        if (_items.Any(i => string.Equals(i.Key, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            throw new DomainException(
                "inventory.duplicate_key",
                $"Feature '{normalized}' is already catalogued.");
        }

        var item = FeatureInventoryItem.Create(Id, normalized, description);
        _items.Add(item);
        Touch(clock);
        return item;
    }

    /// <summary>
    /// Features that still block handoff: catalogued but neither covered nor waived.
    /// </summary>
    public IReadOnlyList<FeatureInventoryItem> BlockingItems =>
        [.. _items.Where(i => i.Coverage == CoverageState.Unaddressed)];

    /// <summary>
    /// The handoff gate for a redesign project. Throws rather than returning a
    /// bool so a caller cannot ignore the result.
    /// </summary>
    public void EnsureReadyForHandoff()
    {
        var blocking = BlockingItems;

        if (blocking.Count > 0)
        {
            throw new DomainException(
                "inventory.uncovered_features",
                $"{blocking.Count} catalogued feature(s) are neither covered nor waived: " +
                string.Join(", ", blocking.Take(10).Select(i => i.Key)));
        }
    }
}

public sealed class FeatureInventoryItem : Entity
{
    private FeatureInventoryItem() { } // EF Core

    public Guid InventoryId { get; private set; }
    public string Key { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public CoverageState Coverage { get; private set; }

    private readonly List<Guid> _coveringScreenIds = [];

    /// <summary>
    /// Screens claimed to cover this feature. Empty unless <see cref="Coverage"/>
    /// is Covered.
    ///
    /// Exposed as a non-castable wrapper over a private list. Handing out the live
    /// list would let a caller clear it after <see cref="MarkCovered"/> validated
    /// it, leaving an item that is Covered with nothing covering it — and
    /// <see cref="FeatureInventory.EnsureReadyForHandoff"/>, which only blocks on
    /// Unaddressed, would then pass. That is the no-functional-loss gate
    /// (ADR-0007) defeated with one method call.
    /// Covered by FeatureInventoryTests.Covering_screens_cannot_be_cleared_from_outside_the_aggregate.
    /// </summary>
    public IReadOnlyList<Guid> CoveringScreenIds => _coveringScreenIds.AsReadOnly();

    public string? WaiverReason { get; private set; }
    public string? WaivedBy { get; private set; }

    internal static FeatureInventoryItem Create(Guid inventoryId, string key, string description) => new()
    {
        InventoryId = inventoryId,
        Key = key,
        Description = description ?? string.Empty,
        Coverage = CoverageState.Unaddressed,
    };

    public void MarkCovered(IEnumerable<Guid> screenIds)
    {
        var ids = screenIds?.Where(id => id != Guid.Empty).Distinct().ToList() ?? [];

        if (ids.Count == 0)
        {
            throw new DomainException(
                "inventory.covering_screen_required",
                "Marking a feature covered requires at least one covering screen.");
        }

        Coverage = CoverageState.Covered;
        _coveringScreenIds.Clear();
        _coveringScreenIds.AddRange(ids);
        WaiverReason = null;
        WaivedBy = null;
    }

    /// <summary>
    /// Records a deliberate decision to drop a feature. Both a reason and an owner
    /// are mandatory: an unattributed waiver is indistinguishable from an accident.
    /// </summary>
    public void Waive(string reason, string waivedBy)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException(
                "inventory.waiver_reason_required",
                "Dropping a feature requires a recorded reason.");
        }

        if (string.IsNullOrWhiteSpace(waivedBy))
        {
            throw new DomainException(
                "inventory.waiver_owner_required",
                "Dropping a feature requires an accountable owner.");
        }

        Coverage = CoverageState.Waived;
        WaiverReason = reason.Trim();
        WaivedBy = waivedBy.Trim();
        _coveringScreenIds.Clear();
    }
}
