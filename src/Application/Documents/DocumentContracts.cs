using AISupportOps.Domain.Documents;

namespace AISupportOps.Application.Documents;

public sealed record DocumentResponse(
    Guid Id,
    string FileName,
    DocumentKind Kind,
    string ContentType,
    long SizeBytes,
    DocumentStatus Status,
    int ChunkCount,
    string? Error,
    Guid UploadedByUserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ProcessedAt);

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed record DocumentChunkResponse(Guid Id, int Index, string Content, int TokenCount, int? PageNumber, string? Heading);

public sealed record DocumentContent(Stream Content, string FileName, string ContentType);
