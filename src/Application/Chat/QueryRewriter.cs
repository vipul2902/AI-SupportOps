using System.Text;
using AISupportOps.Application.Ai;
using AISupportOps.Application.Knowledge;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AISupportOps.Application.Chat;

/// <summary>
/// Turns a follow-up ("what about on mobile?") into a standalone question ("How do I reset my password
/// on the mobile app?") before retrieval. Without it, the embedding of a follow-up carries none of the
/// topic and retrieval fails. Costs one small LLM call, and only when there is history.
/// </summary>
public sealed partial class QueryRewriter(
    IAiChatService llm,
    IOptions<ConversationOptions> options,
    ILogger<QueryRewriter> logger)
{
    private const int MaxTurnLength = 500;

    public async Task<string> RewriteAsync(IReadOnlyList<LlmMessage> history, string followUp, CancellationToken ct)
    {
        if (history.Count == 0)
        {
            return followUp;
        }

        var conversation = new StringBuilder();
        foreach (var turn in history)
        {
            var text = turn.Content.Length > MaxTurnLength ? turn.Content[..MaxTurnLength] + "…" : turn.Content;
            conversation.Append(turn.Role == LlmRole.User ? "User: " : "Assistant: ")
                .AppendLine(text.ReplaceLineEndings(" "));
        }

        var request = new LlmRequest(
            PromptLibrary.Get(options.Value.RewritePromptId),
            [new LlmMessage(LlmRole.User, $"""
                Conversation:
                {conversation}
                <follow_up>
                {RagContextBuilder.EscapeContent(followUp).Replace("</follow_up", "&lt;/follow_up", StringComparison.OrdinalIgnoreCase)}
                </follow_up>
                """)],
            MaxOutputTokens: 150,
            Temperature: 0f);

        try
        {
            var rewritten = (await llm.CompleteAsync(request, ct)).Text.Trim().Trim('"');
            var valid = rewritten.Length is > 0 and <= RetrievalQuery.MaxQueryLength
                && !rewritten.Contains(RagService.NoAnswerMessage, StringComparison.OrdinalIgnoreCase);
            return valid ? rewritten : followUp;
        }
        catch (AiUnavailableException ex)
        {
            // Degrade gracefully: retrieval with the raw follow-up is worse, but better than failing the turn.
            LogRewriteFailed(logger, ex);
            return followUp;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Follow-up rewriting failed; using the original message for retrieval")]
    private static partial void LogRewriteFailed(ILogger logger, Exception exception);
}
