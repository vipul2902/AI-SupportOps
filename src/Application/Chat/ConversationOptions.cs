using System.ComponentModel.DataAnnotations;

namespace AISupportOps.Application.Chat;

public sealed class ConversationOptions
{
    public const string SectionName = "Conversations";

    /// <summary>Most recent messages considered as short-term context for the LLM.</summary>
    [Range(0, 50)]
    public int HistoryMaxMessages { get; set; } = 10;

    /// <summary>Token cap on that history. Bounds cost per turn regardless of conversation length.</summary>
    [Range(0, 20_000)]
    public int HistoryMaxTokens { get; set; } = 2000;

    [Required]
    public string RewritePromptId { get; set; } = "query-rewrite.v1";

    [Range(1, 20_000)]
    public int MaxMessageLength { get; set; } = 4000;

    [Required]
    public string SummaryPromptId { get; set; } = "conversation-summary.v1";

    /// <summary>Summarize once at least this many messages have left the window: one LLM call per batch, not per turn.</summary>
    [Range(1, 100)]
    public int SummarizeBatchSize { get; set; } = 6;
}
