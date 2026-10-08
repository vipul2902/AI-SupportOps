using AISupportOps.Domain.Common;

namespace AISupportOps.Domain.Documents;

public enum DocumentStatus
{
    Uploaded = 0,
    Processing = 1,
    Processed = 2,
    Failed = 3,
}

public enum DocumentKind
{
    Pdf = 0,
    PlainText = 1,
    Markdown = 2,
    Docx = 3,
}

/// <summary>
/// An uploaded knowledge source. Bytes live in file storage under <see cref="StorageKey"/>;
/// this row tracks metadata and the ingestion lifecycle:
/// Uploaded → Processing → Processed | Failed (Failed may be retried → Processing).
/// </summary>
public sealed class Document : Entity, ITenantOwned
{
    public const int FileNameMaxLength = 255;
    public const int ErrorMaxLength = 1000;

    private Document()
    {
    }

    public Document(Guid tenantId, string fileName, DocumentKind kind, string contentType, long sizeBytes, string sha256, string storageKey, Guid uploadedByUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);

        TenantId = tenantId;
        FileName = fileName;
        Kind = kind;
        ContentType = contentType;
        SizeBytes = sizeBytes;
        Sha256 = sha256;
        UploadedByUserId = uploadedByUserId;
        StorageKey = storageKey;
        Status = DocumentStatus.Uploaded;
    }

    public Guid TenantId { get; private set; }

    /// <summary>Sanitized original file name, for display only — never used as a path.</summary>
    public string FileName { get; private set; } = string.Empty;

    public DocumentKind Kind { get; private set; }

    public string ContentType { get; private set; } = string.Empty;

    public long SizeBytes { get; private set; }

    /// <summary>Hex SHA-256 of the content; detects duplicate uploads within a tenant.</summary>
    public string Sha256 { get; private set; } = string.Empty;

    public string StorageKey { get; private set; } = string.Empty;

    public Guid UploadedByUserId { get; private set; }

    public DocumentStatus Status { get; private set; }

    public int ChunkCount { get; private set; }

    public string? Error { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public void MarkProcessing()
    {
        if (Status is not (DocumentStatus.Uploaded or DocumentStatus.Failed))
        {
            throw new InvalidOperationException($"Cannot start processing a document in status {Status}.");
        }

        Status = DocumentStatus.Processing;
        Error = null;
    }

    public void MarkProcessed(int chunkCount, DateTimeOffset now)
    {
        if (Status != DocumentStatus.Processing)
        {
            throw new InvalidOperationException($"Cannot complete a document in status {Status}.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(chunkCount);
        Status = DocumentStatus.Processed;
        ChunkCount = chunkCount;
        ProcessedAt = now;
    }

    public void MarkFailed(string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        Status = DocumentStatus.Failed;
        Error = error.Length > ErrorMaxLength ? error[..ErrorMaxLength] : error;
    }
}
