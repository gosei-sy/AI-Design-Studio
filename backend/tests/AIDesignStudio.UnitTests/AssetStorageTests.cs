using System.Security.Cryptography;
using System.Text;
using AIDesignStudio.Application.Storage;
using AIDesignStudio.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace AIDesignStudio.UnitTests;

/// <summary>
/// Covers the local asset storage adapter, including its path-traversal guards.
/// Storage keys are the only client-influenced value that reaches the
/// filesystem, so they are the one place a tenant boundary could be crossed.
/// </summary>
public sealed class AssetStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"aids-storage-{Guid.CreateVersion7():N}");

    private readonly LocalFileSystemAssetStorage _storage;

    public AssetStorageTests() =>
        _storage = new LocalFileSystemAssetStorage(
            Options.Create(new LocalAssetStorageOptions { RootPath = _root }),
            NullLogger<LocalFileSystemAssetStorage>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static MemoryStream Bytes(string content) => new(Encoding.UTF8.GetBytes(content));

    [Fact]
    public async Task A_saved_asset_round_trips_with_a_correct_size_and_hash()
    {
        const string content = "screenshot-bytes";
        var key = IAssetStorage.BuildKey(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "png");

        var stored = await _storage.SaveAsync(
            key, Bytes(content), "image/png", CancellationToken.None);

        stored.StorageKey.ShouldBe(key);
        stored.SizeBytes.ShouldBe(Encoding.UTF8.GetByteCount(content));
        stored.ContentHash.ShouldBe(
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))));

        await using var read = await _storage.OpenReadAsync(key, CancellationToken.None);
        using var reader = new StreamReader(read);
        (await reader.ReadToEndAsync(CancellationToken.None)).ShouldBe(content);
    }

    [Fact]
    public async Task Exists_and_Delete_behave_as_expected()
    {
        var key = IAssetStorage.BuildKey(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "png");

        (await _storage.ExistsAsync(key, CancellationToken.None)).ShouldBeFalse();

        await _storage.SaveAsync(key, Bytes("x"), "image/png", CancellationToken.None);
        (await _storage.ExistsAsync(key, CancellationToken.None)).ShouldBeTrue();

        await _storage.DeleteAsync(key, CancellationToken.None);
        (await _storage.ExistsAsync(key, CancellationToken.None)).ShouldBeFalse();

        // Deleting an absent key is a no-op, so cleanup is idempotent.
        await Should.NotThrowAsync(() => _storage.DeleteAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task Reading_a_missing_asset_reports_not_found()
    {
        var key = IAssetStorage.BuildKey(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "png");

        await Should.ThrowAsync<FileNotFoundException>(() =>
            _storage.OpenReadAsync(key, CancellationToken.None));
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("org/x/../../../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("..\\..\\windows\\system32")]
    [InlineData("C:/windows/system32")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_key_that_escapes_the_storage_root_is_rejected(string key)
    {
        await Should.ThrowAsync<InvalidStorageKeyException>(() =>
            _storage.SaveAsync(key, Bytes("x"), "image/png", CancellationToken.None));

        await Should.ThrowAsync<InvalidStorageKeyException>(() =>
            _storage.OpenReadAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task A_key_containing_a_control_character_is_rejected()
    {
        await Should.ThrowAsync<InvalidStorageKeyException>(() =>
            _storage.SaveAsync("org/a\0b", Bytes("x"), "image/png", CancellationToken.None));
    }

    /// <summary>
    /// Organization id leads the key so that a mis-scoped query cannot silently
    /// reach another tenant's blobs.
    /// </summary>
    [Fact]
    public void BuildKey_puts_the_organization_first_and_normalizes_the_extension()
    {
        var org = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        var asset = Guid.CreateVersion7();

        var key = IAssetStorage.BuildKey(org, project, asset, ".PNG");

        key.ShouldStartWith($"org/{org:N}/");
        key.ShouldBe($"org/{org:N}/project/{project:N}/asset/{asset:N}.png");
    }

    [Fact]
    public void BuildKey_handles_a_missing_extension()
    {
        var key = IAssetStorage.BuildKey(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "");

        key.ShouldNotEndWith(".");
        Path.GetExtension(key).ShouldBeEmpty();
    }

    [Fact]
    public void The_backend_identifies_itself_for_diagnostics() =>
        _storage.BackendId.ShouldBe("local-filesystem");
}
