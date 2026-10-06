using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;

namespace Slideshow.Api.Storage;

/// <summary>Stores media bytes by server-generated name ("{ownerId}/{albumId}/{mediaId}").</summary>
public interface IBlobStore
{
    Task UploadAsync(string name, Stream content, string contentType, CancellationToken ct);
    /// <summary>A seekable stream (so Range requests work), or null if the blob doesn't exist.</summary>
    Task<Stream?> OpenReadAsync(string name, CancellationToken ct);
    Task DeleteAsync(string name, CancellationToken ct);
    /// <summary>Deletes every blob whose name starts with prefix (which must end in '/').</summary>
    Task DeletePrefixAsync(string prefix, CancellationToken ct);
}

public static class BlobNames
{
    public static string For(string ownerId, Guid albumId, Guid mediaId) => $"{ownerId}/{albumId:N}/{mediaId:N}";
    public static string AlbumPrefix(string ownerId, Guid albumId) => $"{ownerId}/{albumId:N}/";
    public static string UserPrefix(string ownerId) => $"{ownerId}/";
    /// <summary>A photo's preview, stored next to it (so album and account deletes remove it too).</summary>
    public static string Thumbnail(string blobName) => blobName + ".thumb";
}

public sealed class AzureBlobStore : IBlobStore
{
    private readonly BlobContainerClient _container;

    public AzureBlobStore(IOptions<StorageOptions> options)
    {
        var o = options.Value;
        var service = !string.IsNullOrEmpty(o.ConnectionString)
            ? new BlobServiceClient(o.ConnectionString)
            : new BlobServiceClient(new Uri(o.BlobServiceUri ?? throw new InvalidOperationException("Storage:BlobServiceUri is not set.")),
                new DefaultAzureCredential());
        _container = service.GetBlobContainerClient(o.ContainerName);
    }

    /// <summary>Creates the container if missing (private access only).</summary>
    public Task EnsureContainerAsync(CancellationToken ct) => _container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);

    public async Task UploadAsync(string name, Stream content, string contentType, CancellationToken ct) =>
        await _container.GetBlobClient(name).UploadAsync(content,
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } }, ct);

    public async Task<Stream?> OpenReadAsync(string name, CancellationToken ct)
    {
        try { return await _container.GetBlobClient(name).OpenReadAsync(new BlobOpenReadOptions(allowModifications: false), ct); }
        catch (Azure.RequestFailedException e) when (e.Status == 404) { return null; }
    }

    public Task DeleteAsync(string name, CancellationToken ct) =>
        _container.GetBlobClient(name).DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct);

    public async Task DeletePrefixAsync(string prefix, CancellationToken ct)
    {
        if (!prefix.EndsWith('/')) throw new ArgumentException("Prefix must end with '/'.", nameof(prefix));
        await foreach (var blob in _container.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, ct))
            await _container.DeleteBlobIfExistsAsync(blob.Name, DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct);
    }
}

/// <summary>Local development and tests: blobs are files under a root folder.</summary>
public sealed class FileSystemBlobStore(IOptions<StorageOptions> options) : IBlobStore
{
    private readonly string _root = Path.GetFullPath(options.Value.RootPath);

    private string PathFor(string name)
    {
        var path = Path.GetFullPath(Path.Combine(_root, name));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Blob name escapes the storage root.", nameof(name));
        return path;
    }

    public async Task UploadAsync(string name, Stream content, string contentType, CancellationToken ct)
    {
        var path = PathFor(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true); // overwrites, like Azure
        await content.CopyToAsync(file, ct);
    }

    public Task<Stream?> OpenReadAsync(string name, CancellationToken ct)
    {
        var path = PathFor(name);
        Stream? s = File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, useAsync: true) : null;
        return Task.FromResult(s);
    }

    public Task DeleteAsync(string name, CancellationToken ct)
    {
        File.Delete(PathFor(name)); // no error if missing, like Azure's DeleteIfExists
        return Task.CompletedTask;
    }

    public Task DeletePrefixAsync(string prefix, CancellationToken ct)
    {
        if (!prefix.EndsWith('/')) throw new ArgumentException("Prefix must end with '/'.", nameof(prefix));
        var dir = PathFor(prefix.TrimEnd('/'));
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        return Task.CompletedTask;
    }
}
