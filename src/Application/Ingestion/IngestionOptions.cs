using System.ComponentModel.DataAnnotations;

namespace AISupportOps.Application.Ingestion;

public sealed class IngestionOptions
{
    public const string SectionName = "Ingestion";

    /// <summary>Run the background worker in this process.</summary>
    public bool WorkerEnabled { get; set; } = true;

    [Range(typeof(TimeSpan), "00:00:00.050", "00:10:00")]
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>A Processing document untouched for this long is assumed abandoned and re-claimed.</summary>
    [Range(typeof(TimeSpan), "00:00:10", "02:00:00")]
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(10);

    [Range(1, 10)]
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    /// Target chunk size. ~500 tokens keeps a chunk focused on one topic (better retrieval precision)
    /// while carrying enough context to answer from; several fit in a prompt.
    /// </summary>
    [Range(64, 4000)]
    public int ChunkMaxTokens { get; set; } = 512;

    /// <summary>Tokens repeated from the end of the previous chunk so facts spanning a boundary stay retrievable.</summary>
    [Range(0, 1000)]
    public int ChunkOverlapTokens { get; set; } = 64;

    /// <summary>Chunks per embedding API call. Batching cuts per-request overhead; providers cap batch size.</summary>
    [Range(1, 2048)]
    public int EmbeddingBatchSize { get; set; } = 64;

    /// <summary>Guards against decompression bombs (DOCX is a ZIP) and pathological files.</summary>
    [Range(1000, 50_000_000)]
    public int MaxExtractedCharacters { get; set; } = 5_000_000;
}
