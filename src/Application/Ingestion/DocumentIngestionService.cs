using System.Diagnostics;
using AISupportOps.Application.Common;
using AISupportOps.Application.Documents;
using AISupportOps.Domain.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AISupportOps.Application.Ingestion;

/// <summary>
/// Processes one claimed document: extract → normalize → chunk → store. Idempotent: re-running
/// replaces the document's chunks in a single SaveChanges (one transaction), so a crash mid-way
/// never leaves a half-indexed document visible as Processed.
/// Must run inside a tenant scope for the document's tenant.
/// </summary>
public sealed partial class DocumentIngestionService(
    IApplicationDbContext db,
    IFileStorage storage,
    IEnumerable<ITextExtractor> extractors,
    ITokenCounter tokenCounter,
    IOptions<IngestionOptions> options,
    TimeProvider time,
    ILogger<DocumentIngestionService> logger)
{
    public const string NoTextError =
        "No extractable text was found. Scanned or image-only PDFs require OCR, which is not supported yet.";

    public async Task ProcessAsync(Guid documentId, CancellationToken ct)
    {
        var document = await db.Documents.SingleOrDefaultAsync(d => d.Id == documentId, ct);
        if (document is not { Status: DocumentStatus.Processing })
        {
            return; // Deleted or requeued meanwhile; nothing to do.
        }

        var settings = options.Value;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var chunks = await ExtractAndChunkAsync(document, settings, ct);

            var existing = await db.DocumentChunks.Where(c => c.DocumentId == document.Id).ToListAsync(ct);
            db.DocumentChunks.RemoveRange(existing);
            db.DocumentChunks.AddRange(chunks.Select((c, i) =>
                new DocumentChunk(document.TenantId, document.Id, i, c.Content, c.TokenCount, c.PageNumber, c.Heading)));

            document.MarkProcessed(chunks.Count, time.GetUtcNow());
            await db.SaveChangesAsync(ct);

            var totalTokens = chunks.Sum(c => c.TokenCount);
            LogProcessed(logger, document.Id, document.TenantId, chunks.Count, totalTokens, stopwatch.ElapsedMilliseconds);
        }
        catch (DocumentExtractionException ex)
        {
            // Permanent: the file itself is the problem. Retrying will not help.
            LogPermanentFailure(logger, document.Id, ex.Message);
            await RecordFailureAsync(document.Id, d => d.MarkFailed(ex.Message));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Possibly transient (storage/database hiccup): retry until the attempt budget is spent.
            LogTransientFailure(logger, ex, document.Id, document.ProcessingAttempts, settings.MaxAttempts);
            await RecordFailureAsync(document.Id, d =>
            {
                if (d.ProcessingAttempts >= settings.MaxAttempts)
                {
                    d.MarkFailed($"Processing failed after {d.ProcessingAttempts} attempts.");
                }
                else
                {
                    d.ReleaseForRetry();
                }
            });
        }
    }

    /// <summary>Discards any partial changes (e.g. half-added chunks) and persists only the status change.</summary>
    private async Task RecordFailureAsync(Guid documentId, Action<Document> apply)
    {
        db.ChangeTracker.Clear();
        var document = await db.Documents.SingleOrDefaultAsync(d => d.Id == documentId, CancellationToken.None);
        if (document is { Status: DocumentStatus.Processing })
        {
            apply(document);
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private async Task<List<TextChunk>> ExtractAndChunkAsync(Document document, IngestionOptions settings, CancellationToken ct)
    {
        var extractor = extractors.FirstOrDefault(e => e.Kind == document.Kind)
            ?? throw new DocumentExtractionException($"No text extractor is registered for {document.Kind} files.");

        IReadOnlyList<ExtractedSection> raw;
        await using (var content = await storage.OpenReadAsync(document.StorageKey, ct))
        {
            raw = await extractor.ExtractAsync(content, ct);
        }

        var sections = raw
            .Select(s => s with { Text = TextNormalizer.Normalize(s.Text) })
            .Where(s => s.Text.Length > 0)
            .ToList();

        if (sections.Sum(s => (long)s.Text.Length) > settings.MaxExtractedCharacters)
        {
            throw new DocumentExtractionException("The document contains more text than the configured limit.");
        }

        if (sections.Count == 0)
        {
            throw new DocumentExtractionException(NoTextError);
        }

        var chunker = new TextChunker(tokenCounter, settings.ChunkMaxTokens, settings.ChunkOverlapTokens);
        return chunker.Chunk(sections, extractor.EmitsMarkdownHeadings).ToList();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Document {DocumentId} (tenant {TenantId}) processed: {ChunkCount} chunks, {TokenCount} tokens in {ElapsedMs} ms")]
    private static partial void LogProcessed(ILogger logger, Guid documentId, Guid tenantId, int chunkCount, int tokenCount, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Document {DocumentId} failed permanently: {Reason}")]
    private static partial void LogPermanentFailure(ILogger logger, Guid documentId, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Document {DocumentId} processing attempt {Attempt}/{MaxAttempts} failed")]
    private static partial void LogTransientFailure(ILogger logger, Exception exception, Guid documentId, int attempt, int maxAttempts);
}
