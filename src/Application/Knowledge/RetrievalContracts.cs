using System.ComponentModel.DataAnnotations;

namespace AISupportOps.Application.Knowledge;

public sealed record SearchRequest(
    [Required, StringLength(RetrievalQuery.MaxQueryLength, MinimumLength = 1)] string Query,
    [Range(1, RetrievalQuery.MaxTopK)] int TopK = 5,
    IReadOnlyList<Guid>? DocumentIds = null,
    [Range(-1.0, 1.0)] double MinScore = 0.0);

public sealed record RetrievedChunk(
    Guid ChunkId,
    Guid DocumentId,
    string FileName,
    string Content,
    int? PageNumber,
    string? Heading,
    double Score);

public sealed record SearchResponse(string Query, IReadOnlyList<RetrievedChunk> Results, long ElapsedMs);

/// <summary>A normalized retrieval request. The tenant is never part of it: it comes from the tenant context.</summary>
public sealed record RetrievalQuery(string Text, int TopK, IReadOnlyList<Guid> DocumentIds, double MinScore)
{
    public const int MaxQueryLength = 2000;
    public const int MaxTopK = 20;
}

/// <summary>
/// Semantic search over the current tenant's processed documents.
/// Implementations MUST constrain results to the current tenant.
/// </summary>
public interface IRetrievalService
{
    Task<IReadOnlyList<RetrievedChunk>> SearchAsync(RetrievalQuery query, CancellationToken ct);
}
