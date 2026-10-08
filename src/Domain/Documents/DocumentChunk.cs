using AISupportOps.Domain.Common;

namespace AISupportOps.Domain.Documents;

/// <summary>
/// A retrieval unit: a passage of a document small enough to embed and to place in an LLM
/// prompt, with the metadata needed to cite it (page, heading). Embeddings are added in Phase 5.
/// </summary>
public sealed class DocumentChunk : Entity, ITenantOwned
{
    public const int HeadingMaxLength = 300;

    private DocumentChunk()
    {
    }

    public DocumentChunk(Guid tenantId, Guid documentId, int index, string content, int tokenCount, int? pageNumber, string? heading)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tokenCount);

        TenantId = tenantId;
        DocumentId = documentId;
        Index = index;
        Content = content;
        TokenCount = tokenCount;
        PageNumber = pageNumber;
        Heading = heading is { Length: > HeadingMaxLength } ? heading[..HeadingMaxLength] : heading;
    }

    public Guid TenantId { get; private set; }

    public Guid DocumentId { get; private set; }

    /// <summary>0-based position within the document; preserves reading order.</summary>
    public int Index { get; private set; }

    public string Content { get; private set; } = string.Empty;

    public int TokenCount { get; private set; }

    /// <summary>1-based page where the chunk starts (PDF only).</summary>
    public int? PageNumber { get; private set; }

    /// <summary>Nearest preceding section heading (Markdown/DOCX), used for citations and context.</summary>
    public string? Heading { get; private set; }
}
