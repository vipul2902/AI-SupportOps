using System.Diagnostics;
using AISupportOps.Application.Common;
using AISupportOps.Application.Documents;
using AISupportOps.Application.Knowledge;
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
    IEmbeddingService embeddingService,
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
        using var activity = Telemetry.Source.StartActivity("ingestion.process");
        activity?.SetTag("document.id", document.Id);
        activity?.SetTag("document.kind", document.Kind.ToString());
        activity?.SetTag("document.size_bytes", document.SizeBytes);
        activity?.SetTag("document.attempt", document.ProcessingAttempts);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var chunks = await ExtractAndChunkAsync(document, settings, ct);

            var existing = await db.DocumentChunks.Where(c => c.DocumentId == document.Id).ToListAsync(ct);
            db.DocumentChunks.RemoveRange(existing);
            var entities = chunks.Select((c, i) =>
                new DocumentChunk(document.TenantId, document.Id, i, c.Content, c.TokenCount, c.PageNumber, c.Heading)).ToList();
            var embeddingTokens = await EmbedAsync(document, entities, settings.EmbeddingBatchSize, ct);
            db.DocumentChunks.AddRange(entities);

            document.MarkProcessed(chunks.Count, time.GetUtcNow());
            await db.SaveChangesAsync(ct);

            var totalTokens = chunks.Sum(c => c.TokenCount);
            Telemetry.DocumentsProcessed.Add(1, new KeyValuePair<string, object?>("result", "processed"));
            Telemetry.ChunksCreated.Add(chunks.Count);
            Telemetry.IngestionDuration.Record(stopwatch.ElapsedMilliseconds, new KeyValuePair<string, object?>("kind", document.Kind.ToString()));
            activity?.SetTag("document.chunks", chunks.Count);
            LogProcessed(logger, document.Id, document.TenantId, chunks.Count, totalTokens, embeddingTokens, stopwatch.ElapsedMilliseconds);
        }
        catch (DocumentExtractionException ex)
        {
            // Permanent: the file itself is the problem. Retrying will not help.
            LogPermanentFailure(logger, document.Id, ex.Message);
            Telemetry.DocumentsProcessed.Add(1, new KeyValuePair<string, object?>("result", "failed"));
            activity.SetError(ex.Message);
            await RecordFailureAsync(document.Id, d => d.MarkFailed(ex.Message));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Possibly transient (storage/database hiccup): retry until the attempt budget is spent.
            LogTransientFailure(logger, ex, document.Id, document.ProcessingAttempts, settings.MaxAttempts);
            Telemetry.DocumentsProcessed.Add(1, new KeyValuePair<string, object?>("result", "retry"));
            activity?.AddException(ex);
            activity.SetError("transient failure");
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

    /// <summary>
    /// Embeds chunks in batches. The embedded text is prefixed with the document name and section
    /// heading ("contextual chunk header"): a chunk saying "Click Reset" is far easier to match to
    /// "how do I reset my password" when it is known to come from "Account security". The stored
    /// content stays unprefixed.
    /// </summary>
    private async Task<long> EmbedAsync(Document document, List<DocumentChunk> chunks, int batchSize, CancellationToken ct)
    {
        long tokens = 0;
        foreach (var batch in chunks.Chunk(batchSize))
        {
            var inputs = batch.Select(c => ContextualText(document.FileName, c)).ToList();
            var result = await embeddingService.EmbedAsync(inputs, ct);
            for (var i = 0; i < batch.Length; i++)
            {
                batch[i].SetEmbedding(result.Vectors[i], embeddingService.ModelId);
            }

            tokens += result.InputTokens ?? 0;
        }

        return tokens;
    }

    public static string ContextualText(string fileName, DocumentChunk chunk) =>
        chunk.Heading is null
            ? $"{fileName}\n\n{chunk.Content}"
            : $"{fileName} > {chunk.Heading}\n\n{chunk.Content}";

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Document {DocumentId} (tenant {TenantId}) processed: {ChunkCount} chunks, {TokenCount} tokens, {EmbeddingTokens} embedding tokens billed, in {ElapsedMs} ms")]
    private static partial void LogProcessed(ILogger logger, Guid documentId, Guid tenantId, int chunkCount, int tokenCount, long embeddingTokens, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Document {DocumentId} failed permanently: {Reason}")]
    private static partial void LogPermanentFailure(ILogger logger, Guid documentId, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Document {DocumentId} processing attempt {Attempt}/{MaxAttempts} failed")]
    private static partial void LogTransientFailure(ILogger logger, Exception exception, Guid documentId, int attempt, int maxAttempts);
}
