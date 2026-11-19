using AIDesignStudio.SharedKernel;

namespace AIDesignStudio.Domain.Assets;

public enum AssetKind
{
    /// <summary>Uploaded raster screenshot of an existing application.</summary>
    Screenshot = 1,

    /// <summary>Self-contained HTML/DOM snapshot captured from a running application.</summary>
    MarkupSnapshot = 2,

    /// <summary>Rendered preview of a generated screen.</summary>
    ScreenRender = 3,

    /// <summary>Design specification document, e.g. a provider DESIGN.md.</summary>
    DesignDocument = 4,
}

/// <summary>
/// Aggregate root of the Assets module: a row of metadata pointing at a blob.
///
/// The database stores only the storage key and integrity metadata; bytes live in
/// the storage backend behind <c>IAssetStorage</c>. <see cref="StorageKey"/> is
/// opaque to callers and is never a client-supplied path, so a client cannot
/// address another organization's blobs.
/// </summary>
public sealed class AssetReference : AggregateRoot
{
    private AssetReference() { } // EF Core

    public Guid OrganizationId { get; private set; }
    public Guid ProjectId { get; private set; }
    public AssetKind Kind { get; private set; }

    /// <summary>Opaque, server-generated key within the storage backend.</summary>
    public string StorageKey { get; private set; } = string.Empty;

    public string ContentType { get; private set; } = string.Empty;
    public long SizeBytes { get; private set; }

    /// <summary>SHA-256 of the stored bytes, for integrity checks and deduplication.</summary>
    public string ContentHash { get; private set; } = string.Empty;

    public string OriginalFileName { get; private set; } = string.Empty;

    public static AssetReference Create(
        ProjectOwnership owner,
        AssetKind kind,
        string storageKey,
        string contentType,
        long sizeBytes,
        string contentHash,
        string originalFileName,
        IClock clock)
    {
        var (organizationId, projectId) = (owner.OrganizationId, owner.ProjectId);

        if (organizationId == Guid.Empty)
        {
            throw new DomainException("asset.organization_required", "An asset must belong to an organization.");
        }

        if (string.IsNullOrWhiteSpace(storageKey))
        {
            throw new DomainException("asset.storage_key_required", "An asset must have a storage key.");
        }

        if (sizeBytes <= 0)
        {
            throw new DomainException("asset.empty", "An asset must contain data.");
        }

        if (string.IsNullOrWhiteSpace(contentHash))
        {
            throw new DomainException("asset.hash_required", "An asset must record a content hash.");
        }

        return new AssetReference
        {
            OrganizationId = organizationId,
            ProjectId = projectId,
            Kind = kind,
            StorageKey = storageKey,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            ContentHash = contentHash,
            OriginalFileName = originalFileName ?? string.Empty,
            CreatedAt = clock.UtcNow,
        };
    }
}
