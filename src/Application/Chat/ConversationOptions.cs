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
}
