using System.ComponentModel.DataAnnotations;

namespace AISupportOps.Application.Knowledge;

public sealed class RagOptions
{
    public const string SectionName = "Rag";

    [Required]
    public string PromptId { get; set; } = "rag-answer.v1";

    /// <summary>Candidates retrieved before thresholding and budgeting.</summary>
    [Range(1, 20)]
    public int TopK { get; set; } = 6;

    /// <summary>
    /// Chunks below this cosine similarity are treated as irrelevant. If none pass, the LLM is not called.
    /// Depends on the embedding model; tune it with the evaluation dataset (Phase 11).
    /// </summary>
    [Range(-1.0, 1.0)]
    public double MinScore { get; set; } = 0.30;

    /// <summary>Upper bound on source text in the prompt. Bounds cost and latency, and avoids "lost in the middle".</summary>
    [Range(200, 100_000)]
    public int MaxContextTokens { get; set; } = 3000;

    [Range(50, 8000)]
    public int MaxOutputTokens { get; set; } = 800;

    /// <summary>Low temperature: factual, repeatable answers rather than creative ones.</summary>
    [Range(0.0, 2.0)]
    public float Temperature { get; set; } = 0.1f;
}
