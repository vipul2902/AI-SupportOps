using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AISupportOps.Application.Documents;

namespace AISupportOps.Infrastructure.Storage;

public sealed class AzureBlobStorageOptions
{
    public const string SectionName = "Storage:AzureBlob";

    /// <summary>Production: account endpoint (https://acct.blob.core.windows.net); auth via managed identity, no keys.</summary>
    public Uri? ServiceUri { get; set; }

    /// <summary>Local development and tests only (Azurite). Never used in Azure.</summary>
    public string? ConnectionString { get; set; }

    public string ContainerName { get; set; } = "documents";
}

/// <summary>
/// Document storage for multi-replica deployments. Local disk does not work on Azure Container Apps:
/// replicas do not share a filesystem (a file uploaded to one is invisible to a worker on another) and
/// container disks are discarded on every new revision. Blob Storage is shared, durable, and private.
/// </summary>
internal sealed class AzureBlobFileStorage(BlobContainerClient container) : IFileStorage
{
    public async Task SaveAsync(string key, Stream content, CancellationToken ct)
    {
        // Keys are generated and unique; refuse to overwrite anything (ETag "*" = must not exist).
        // Uploads are staged as blocks and committed at the end, so a failed or oversized upload never
        // leaves a partial blob visible.
        await Blob(key).UploadAsync(content, new BlobUploadOptions
        {
            Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
        }, ct);
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct)
    {
        try
        {
            return await Blob(key).OpenReadAsync(cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            throw new FileNotFoundException("Stored file not found.", key, ex);
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct) =>
        await Blob(key).DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct);

    private BlobClient Blob(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Contains("..", StringComparison.Ordinal) || key.StartsWith('/') || key.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException("Invalid storage key.", nameof(key));
        }

        return container.GetBlobClient(key);
    }
}
