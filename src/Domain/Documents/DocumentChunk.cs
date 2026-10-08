using AISupportOps.Domain.Common;

namespace AISupportOps.Domain.Documents;

/// <summary>
/// A retrieval unit: a passage of a document small enough to embed and to place in an LLM
/// prompt, with the metadata needed to cite it (page, heading) and its embedding for semantic search.
/// </summary>
public sealed class DocumentChunk : Entity, ITenantOwned
{
    public const int HeadingMaxLength = 300;

    /// <summary>
    /// Vector size of the embedding column. Fixed by the schema (vector(1536)); matches
    /// OpenAI text-embedding-3-small. Changing models with a different size needs a migration + re-index.
    /// </summary>
    public const int EmbeddingDimensions = 1536;

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

    /// <summary>Semantic vector of the chunk; null until embedded.</summary>
    public float[]? Embedding { get; private set; }

    /// <summary>Model that produced <see cref="Embedding"/>. Vectors from different models are not comparable.</summary>
    public string? EmbeddingModel { get; private set; }

    public void SetEmbedding(float[] embedding, string model)
    {
        ArgumentNullException.ThrowIfNull(embedding);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (embedding.Length != EmbeddingDimensions)
        {
            throw new ArgumentException($"Expected {EmbeddingDimensions} dimensions but got {embedding.Length}.", nameof(embedding));
        }

        Embedding = embedding;
        EmbeddingModel = model;
    }
}
