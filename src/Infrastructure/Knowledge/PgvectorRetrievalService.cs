using System.Diagnostics;
using AISupportOps.Application.Common;
using AISupportOps.Application.Knowledge;
using AISupportOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pgvector;

namespace AISupportOps.Infrastructure.Knowledge;

public sealed class RetrievalOptions
{
    public const string SectionName = "Retrieval";

    /// <summary>
    /// HNSW search breadth. Higher = better recall, slower. Must be ≥ TopK; pgvector default is 40.
    /// </summary>
    public int EfSearch { get; set; } = 100;
}

/// <summary>
/// Vector similarity search in PostgreSQL. Hand-written SQL so the tenant predicate is explicit and
/// reviewable (in addition to the EF global filter, which raw SQL bypasses) and so the ORDER BY
/// matches the HNSW index expression exactly (<c>embedding &lt;=&gt; @query</c>, cosine distance).
/// </summary>
internal sealed partial class PgvectorRetrievalService(
    AppDbContext db,
    IEmbeddingService embeddings,
    ITenantContext tenantContext,
    IOptions<RetrievalOptions> options,
    ILogger<PgvectorRetrievalService> logger) : IRetrievalService
{
    // Column names follow the snake_case naming convention, which EF also applies to ad-hoc result types.
    private sealed record Row(Guid ChunkId, Guid DocumentId, string FileName, string Content, int? PageNumber, string? Heading, double Score);

    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(RetrievalQuery query, CancellationToken ct)
    {
        // Fail closed: no tenant, no results. Never run an unscoped vector search.
        var tenantId = tenantContext.TenantId
            ?? throw new InvalidOperationException("Vector search requires a tenant context.");

        var embedStopwatch = Stopwatch.StartNew();
        var vector = new Vector(await embeddings.EmbedQueryAsync(query.Text, ct));
        var embedMs = embedStopwatch.ElapsedMilliseconds;

        var searchStopwatch = Stopwatch.StartNew();
        var documentIds = query.DocumentIds.ToArray();
        var efSearch = Math.Max(options.Value.EfSearch, query.TopK).ToString(System.Globalization.CultureInfo.InvariantCulture);

        // SET LOCAL-style settings only live for this transaction.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // HNSW returns ef_search nearest candidates and *then* applies WHERE filters. With many tenants
        // sharing one index, a tenant could get fewer than TopK results. Iterative scan (pgvector 0.8+)
        // keeps scanning the graph until enough rows pass the filter.
        await db.Database.ExecuteSqlAsync($"SELECT set_config('hnsw.iterative_scan', 'relaxed_order', true), set_config('hnsw.ef_search', {efSearch}, true)", ct);

        var rows = await db.Database.SqlQuery<Row>($"""
            SELECT c.id          AS chunk_id,
                   c.document_id,
                   d.file_name,
                   c.content,
                   c.page_number,
                   c.heading,
                   1 - (c.embedding <=> {vector}) AS score
            FROM document_chunks c
            JOIN documents d ON d.id = c.document_id
            WHERE c.tenant_id = {tenantId}
              AND d.tenant_id = {tenantId}
              AND d.status = 'Processed'
              AND c.embedding IS NOT NULL
              AND c.embedding_model = {embeddings.ModelId}
              AND (cardinality({documentIds}) = 0 OR c.document_id = ANY({documentIds}))
            ORDER BY c.embedding <=> {vector}
            LIMIT {query.TopK}
            """).ToListAsync(ct);

        await transaction.CommitAsync(ct);

        var results = rows
            .Where(r => r.Score >= query.MinScore)
            .Select(r => new RetrievedChunk(r.ChunkId, r.DocumentId, r.FileName, r.Content, r.PageNumber, r.Heading, Math.Round(r.Score, 4)))
            .ToList();

        var topScore = results.Count > 0 ? results[0].Score : 0;
        LogSearch(logger, tenantId, results.Count, topScore, embedMs, searchStopwatch.ElapsedMilliseconds);
        return results;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Vector search for tenant {TenantId}: {ResultCount} results, top score {TopScore}, embed {EmbedMs} ms, search {SearchMs} ms")]
    private static partial void LogSearch(ILogger logger, Guid tenantId, int resultCount, double topScore, long embedMs, long searchMs);
}
