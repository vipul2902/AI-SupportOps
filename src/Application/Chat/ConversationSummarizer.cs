using System.Text;
using AISupportOps.Application.Ai;
using AISupportOps.Application.Common;
using AISupportOps.Domain.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AISupportOps.Application.Chat;

/// <summary>
/// Long-range memory. When messages scroll out of the short-term window, they are folded into a
/// running summary (incrementally: previous summary + newly expired messages), so the conversation
/// keeps its context without the prompt growing. Runs in batches to amortize the LLM call.
/// </summary>
public sealed partial class ConversationSummarizer(
    IApplicationDbContext db,
    IAiChatService llm,
    IOptions<ConversationOptions> options,
    ILogger<ConversationSummarizer> logger)
{
    public const int MaxSummaryLength = 2000;
    private const int MaxMessageLength = 1000;

    /// <returns>True if the summary was updated.</returns>
    public async Task<bool> SummarizeIfNeededAsync(Guid conversationId, CancellationToken ct)
    {
        var settings = options.Value;
        var conversation = await db.Conversations.SingleOrDefaultAsync(c => c.Id == conversationId, ct);
        if (conversation is null)
        {
            return false;
        }

        var unsummarized = await db.Messages
            .Where(m => m.ConversationId == conversationId && m.Status != MessageStatus.Failed)
            .Where(m => conversation.SummarizedUntil == null || m.CreatedAt > conversation.SummarizedUntil)
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .ToListAsync(ct);

        // Only messages that are no longer in the short-term window are folded in.
        var overflow = unsummarized.Count - settings.HistoryMaxMessages;
        if (overflow < settings.SummarizeBatchSize)
        {
            return false;
        }

        var toFold = unsummarized.Take(overflow).ToList();
        var transcript = new StringBuilder();
        foreach (var m in toFold)
        {
            var text = HistoryWindow.StripCitationMarkers(m.Content);
            text = text.Length > MaxMessageLength ? text[..MaxMessageLength] + "…" : text;
            transcript.Append(m.Role == MessageRole.User ? "User: " : "Assistant: ").AppendLine(text.ReplaceLineEndings(" "));
        }

        var request = new LlmRequest(
            PromptLibrary.Get(settings.SummaryPromptId),
            [new LlmMessage(LlmRole.User, $"""
                <previous_summary>
                {conversation.Summary ?? "(none)"}
                </previous_summary>
                <new_messages>
                {transcript}
                </new_messages>
                """)],
            MaxOutputTokens: 300,
            Temperature: 0f);

        try
        {
            var summary = (await llm.CompleteAsync(request, ct)).Text.Trim();
            if (summary.Length == 0)
            {
                return false;
            }

            conversation.UpdateSummary(summary.Length > MaxSummaryLength ? summary[..MaxSummaryLength] : summary, toFold[^1].CreatedAt);
            await db.SaveChangesAsync(ct);
            LogSummarized(logger, conversationId, toFold.Count);
            return true;
        }
        catch (AiUnavailableException ex)
        {
            // Not fatal: the next turn will try again with an even larger batch.
            LogSummaryFailed(logger, ex, conversationId);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Conversation {ConversationId}: folded {MessageCount} messages into the summary")]
    private static partial void LogSummarized(ILogger logger, Guid conversationId, int messageCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Conversation {ConversationId}: summarization failed; will retry next turn")]
    private static partial void LogSummaryFailed(ILogger logger, Exception exception, Guid conversationId);
}
