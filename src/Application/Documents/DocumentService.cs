using AISupportOps.Application.Common;
using AISupportOps.Domain.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AISupportOps.Application.Documents;

/// <summary>
/// Upload, list, download, and delete documents in the current tenant. Processing
/// (text extraction, chunking, embeddings) happens asynchronously in Phase 4.
/// </summary>
public sealed partial class DocumentService(
    IApplicationDbContext db,
    IFileStorage storage,
    ICurrentUser currentUser,
    IOptions<DocumentOptions> options,
    ILogger<DocumentService> logger)
{
    public const int MaxPageSize = 100;

    public async Task<DocumentResponse> UploadAsync(Stream content, string? fileName, CancellationToken ct)
    {
        var tenantId = currentUser.RequireTenantId();
        var userId = currentUser.RequireUserId();
        var maxBytes = options.Value.MaxFileSizeBytes;
        var safeName = FileNames.Sanitize(fileName);

        // 1. Sniff the first bytes and reject disallowed or disguised files before storing anything.
        var header = new byte[FileTypeInspector.HeaderSize];
        var headerLength = await content.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
        var type = FileTypeInspector.Inspect(safeName, header.AsSpan(0, headerLength));
        if (!type.IsAllowed)
        {
            throw new UnsupportedFileException(type.Error!);
        }

        // 2. Stream to storage while hashing and enforcing the size limit.
        var storageKey = $"{tenantId:N}/{Guid.CreateVersion7():N}";
        string sha256;
        long size;
        await using (var upload = new InspectingUploadStream(header.AsMemory(0, headerLength), content, maxBytes))
        {
            try
            {
                await storage.SaveAsync(storageKey, upload, ct);
            }
            catch (FileTooLargeException)
            {
                await DeleteBlobQuietlyAsync(storageKey);
                throw new PayloadTooLargeException($"File exceeds the maximum size of {maxBytes / (1024 * 1024)} MB.");
            }

            sha256 = upload.GetHashHex();
            size = upload.BytesRead;
        }

        // 3. Record metadata. Compensate by deleting the blob if anything fails from here.
        try
        {
            var existing = await db.Documents.Where(d => d.Sha256 == sha256).Select(d => (Guid?)d.Id).FirstOrDefaultAsync(ct);
            if (existing is not null)
            {
                throw new ConflictException($"This file has already been uploaded (document {existing}).");
            }

            var document = new Document(tenantId, safeName, type.Kind, type.ContentType, size, sha256, storageKey, userId);
            db.Documents.Add(document);
            await db.SaveChangesAsync(ct);

            LogUploaded(logger, document.Id, tenantId, type.Kind, size);
            return ToResponse(document);
        }
        catch
        {
            await DeleteBlobQuietlyAsync(storageKey);
            throw;
        }
    }

    public async Task<PagedResponse<DocumentResponse>> ListAsync(DocumentStatus? status, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = db.Documents.AsNoTracking();
        if (status is not null)
        {
            query = query.Where(d => d.Status == status);
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(d => d.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(d => new DocumentResponse(d.Id, d.FileName, d.Kind, d.ContentType, d.SizeBytes, d.Status, d.ChunkCount, d.Error, d.UploadedByUserId, d.CreatedAt, d.ProcessedAt))
            .ToListAsync(ct);

        return new PagedResponse<DocumentResponse>(items, page, pageSize, total);
    }

    public async Task<DocumentResponse> GetAsync(Guid id, CancellationToken ct) =>
        ToResponse(await FindAsync(id, ct));

    public async Task<DocumentContent> OpenContentAsync(Guid id, CancellationToken ct)
    {
        var document = await FindAsync(id, ct);
        var stream = await storage.OpenReadAsync(document.StorageKey, ct);
        return new DocumentContent(stream, document.FileName, document.ContentType);
    }

    /// <summary>Retry a failed document or re-index a processed one.</summary>
    public async Task<DocumentResponse> ReprocessAsync(Guid id, CancellationToken ct)
    {
        var document = await FindAsync(id, ct);
        if (document.Status is not (DocumentStatus.Failed or DocumentStatus.Processed))
        {
            throw new ConflictException($"Document is currently {document.Status} and cannot be requeued.");
        }

        document.Requeue();
        await db.SaveChangesAsync(ct);
        return ToResponse(document);
    }

    public async Task<PagedResponse<DocumentChunkResponse>> ListChunksAsync(Guid id, int page, int pageSize, CancellationToken ct)
    {
        await FindAsync(id, ct); // 404 for unknown or other-tenant documents
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = db.DocumentChunks.AsNoTracking().Where(c => c.DocumentId == id);
        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(c => c.Index)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new DocumentChunkResponse(c.Id, c.Index, c.Content, c.TokenCount, c.PageNumber, c.Heading))
            .ToListAsync(ct);

        return new PagedResponse<DocumentChunkResponse>(items, page, pageSize, total);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var document = await FindAsync(id, ct);
        db.Documents.Remove(document);
        await db.SaveChangesAsync(ct);

        // Metadata is the source of truth; an orphaned blob is harmless and can be swept later.
        await DeleteBlobQuietlyAsync(document.StorageKey);
    }

    private async Task<Document> FindAsync(Guid id, CancellationToken ct) =>
        await db.Documents.SingleOrDefaultAsync(d => d.Id == id, ct)
            ?? throw new NotFoundException("Document not found.");

    private async Task DeleteBlobQuietlyAsync(string key)
    {
        try
        {
            await storage.DeleteAsync(key, CancellationToken.None);
        }
#pragma warning disable CA1031 // Best-effort cleanup must not mask the original outcome.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogBlobCleanupFailed(logger, ex, key);
        }
    }

    private static DocumentResponse ToResponse(Document d) =>
        new(d.Id, d.FileName, d.Kind, d.ContentType, d.SizeBytes, d.Status, d.ChunkCount, d.Error, d.UploadedByUserId, d.CreatedAt, d.ProcessedAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Document {DocumentId} uploaded to tenant {TenantId} ({Kind}, {SizeBytes} bytes)")]
    private static partial void LogUploaded(ILogger logger, Guid documentId, Guid tenantId, DocumentKind kind, long sizeBytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to delete blob {StorageKey}; it is now orphaned")]
    private static partial void LogBlobCleanupFailed(ILogger logger, Exception exception, string storageKey);
}
