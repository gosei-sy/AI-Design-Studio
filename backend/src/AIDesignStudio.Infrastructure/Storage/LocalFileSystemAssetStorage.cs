using System.Security.Cryptography;
using AIDesignStudio.Application.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIDesignStudio.Infrastructure.Storage;

public sealed class LocalAssetStorageOptions
{
    public const string SectionName = "Storage:Local";

    /// <summary>Directory that contains all stored blobs. Created on first write.</summary>
    public string RootPath { get; set; } = "./.local-assets";
}

/// <summary>
/// Development implementation of <see cref="IAssetStorage"/> over the local
/// filesystem. The Azure Blob implementation is a separate adapter behind the
/// same port; nothing above this layer knows which one is active.
///
/// Every key is validated and the resolved absolute path is re-checked against
/// the configured root, so a crafted key cannot write outside the container
/// even if validation is later weakened.
/// </summary>
public sealed class LocalFileSystemAssetStorage : IAssetStorage
{
    private readonly string _rootPath;
    private readonly ILogger<LocalFileSystemAssetStorage> _logger;

    public LocalFileSystemAssetStorage(
        IOptions<LocalAssetStorageOptions> options,
        ILogger<LocalFileSystemAssetStorage> logger)
    {
        _rootPath = Path.GetFullPath(options.Value.RootPath);
        _logger = logger;
    }

    public string BackendId => "local-filesystem";

    public async Task<StoredAsset> SaveAsync(
        string storageKey, Stream content, string contentType, CancellationToken cancellationToken)
    {
        var path = ResolvePath(storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Hash while writing so the stream is read once; uploads can be large and
        // are not always seekable.
        using var sha = SHA256.Create();
        long size = 0;

        await using (var target = File.Create(path))
        await using (var crypto = new CryptoStream(target, sha, CryptoStreamMode.Write, leaveOpen: true))
        {
            var buffer = new byte[81920];
            int read;

            while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await crypto.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                size += read;
            }

            await crypto.FlushFinalBlockAsync(cancellationToken);
        }

        var hash = Convert.ToHexStringLower(sha.Hash!);
        _logger.LogDebug("Stored asset {StorageKey} ({SizeBytes} bytes)", storageKey, size);

        return new StoredAsset(storageKey, size, hash);
    }

    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken)
    {
        var path = ResolvePath(storageKey);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Asset not found: {storageKey}");
        }

        return Task.FromResult<Stream>(File.OpenRead(path));
    }

    public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken) =>
        Task.FromResult(File.Exists(ResolvePath(storageKey)));

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        var path = ResolvePath(storageKey);

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Maps a storage key to an absolute path, rejecting anything that is not a
    /// plain relative key or that escapes <see cref="_rootPath"/>.
    /// </summary>
    private string ResolvePath(string storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey) ||
            storageKey.StartsWith('/') ||
            storageKey.Contains("..", StringComparison.Ordinal) ||
            storageKey.Contains('\\', StringComparison.Ordinal) ||
            storageKey.Contains(':', StringComparison.Ordinal) ||
            storageKey.Any(char.IsControl))
        {
            throw new InvalidStorageKeyException(storageKey);
        }

        var full = Path.GetFullPath(Path.Combine(_rootPath, storageKey));

        // Defence in depth: confirm the resolved path is inside the root.
        if (!full.StartsWith(_rootPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidStorageKeyException(storageKey);
        }

        return full;
    }
}
