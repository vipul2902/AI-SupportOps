namespace AISupportOps.Application.Knowledge;

/// <summary>Vectors for a batch of inputs, in input order, plus billed token usage when the provider reports it.</summary>
public sealed record EmbeddingBatch(IReadOnlyList<float[]> Vectors, long? InputTokens);

/// <summary>
/// Turns text into embedding vectors. Documents and queries must be embedded by the same
/// model, otherwise their vectors live in different spaces and similarity is meaningless.
/// </summary>
public interface IEmbeddingService
{
    string ModelId { get; }

    Task<EmbeddingBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct);
}

public static class EmbeddingServiceExtensions
{
    public static async Task<float[]> EmbedQueryAsync(this IEmbeddingService service, string query, CancellationToken ct) =>
        (await service.EmbedAsync([query], ct)).Vectors[0];
}
