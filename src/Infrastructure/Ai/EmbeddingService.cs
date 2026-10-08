using System.Diagnostics;
using AISupportOps.Application.Knowledge;
using AISupportOps.Domain.Documents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AISupportOps.Infrastructure.Ai;

/// <summary>
/// Adapts any Microsoft.Extensions.AI embedding generator (OpenAI, Azure OpenAI, fake) to the
/// application's port, enforcing the schema's vector size and logging latency and token usage.
/// </summary>
internal sealed partial class EmbeddingService(
    IEmbeddingGenerator<string, Embedding<float>> generator,
    string modelId,
    ILogger<EmbeddingService> logger) : IEmbeddingService
{
    public string ModelId => modelId;

    public async Task<float[]> EmbedQueryAsync(string query, CancellationToken ct) =>
        (await EmbedAsync([query], ct)).Vectors[0];

    public async Task<EmbeddingBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct)
    {
        if (inputs.Count == 0)
        {
            return new EmbeddingBatch([], 0);
        }

        var stopwatch = Stopwatch.StartNew();
        var result = await generator.GenerateAsync(
            inputs,
            new EmbeddingGenerationOptions { Dimensions = DocumentChunk.EmbeddingDimensions },
            ct);

        if (result.Count != inputs.Count)
        {
            throw new InvalidOperationException($"Embedding provider returned {result.Count} vectors for {inputs.Count} inputs.");
        }

        var vectors = result.Select(e => e.Vector.ToArray()).ToList();
        if (vectors.Any(v => v.Length != DocumentChunk.EmbeddingDimensions))
        {
            throw new InvalidOperationException($"Embedding provider returned vectors that are not {DocumentChunk.EmbeddingDimensions}-dimensional.");
        }

        var tokens = result.Usage?.InputTokenCount;
        LogEmbedded(logger, inputs.Count, modelId, tokens, stopwatch.ElapsedMilliseconds);
        return new EmbeddingBatch(vectors, tokens);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Embedded {InputCount} inputs with {Model} ({InputTokens} tokens) in {ElapsedMs} ms")]
    private static partial void LogEmbedded(ILogger logger, int inputCount, string model, long? inputTokens, long elapsedMs);
}
