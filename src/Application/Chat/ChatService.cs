using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using AISupportOps.Application.Ai;
using AISupportOps.Application.Common;
using AISupportOps.Application.Ingestion;
using AISupportOps.Application.Knowledge;
using AISupportOps.Domain.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AISupportOps.Application.Chat;

/// <summary>A validated, persisted user turn, ready to stream an answer for.</summary>
public sealed record ChatTurn(Conversation Conversation, Message UserMessage, IReadOnlyList<LlmMessage> History, IReadOnlyList<Guid> DocumentIds);

/// <summary>
/// Conversational RAG with streaming. Split in two so that everything that can fail with a proper
/// HTTP status (validation, 404, 403) happens in <see cref="BeginTurnAsync"/>, before the response
/// starts. Once streaming begins the status is already 200, so <see cref="StreamTurnAsync"/> reports
/// failures as an "error" event and always persists what happened.
/// </summary>
public sealed partial class ChatService(
    IApplicationDbContext db,
    ICurrentUser currentUser,
    IRagService rag,
    QueryRewriter rewriter,
    ConversationSummarizer summarizer,
    IAiChatService llm,
    ITokenCounter tokenCounter,
    IOptions<ConversationOptions> conversationOptions,
    IOptions<RagOptions> ragOptions,
    TimeProvider time,
    ILogger<ChatService> logger)
{
    public async Task<ChatTurn> BeginTurnAsync(ChatRequest request, CancellationToken ct)
    {
        var settings = conversationOptions.Value;
        var tenantId = currentUser.RequireTenantId();
        var userId = currentUser.RequireUserId();
        var content = request.Message.Trim();
        if (content.Length == 0 || content.Length > settings.MaxMessageLength)
        {
            throw new BusinessRuleException($"Message must be between 1 and {settings.MaxMessageLength} characters.");
        }

        var now = time.GetUtcNow();
        Conversation conversation;
        IReadOnlyList<LlmMessage> history = [];
        if (request.ConversationId is { } conversationId)
        {
            // Private to its creator: another user's conversation is "not found", even for admins.
            conversation = await db.Conversations.SingleOrDefaultAsync(c => c.Id == conversationId && c.UserId == userId, ct)
                ?? throw new NotFoundException("Conversation not found.");

            var recent = await db.Messages
                .Where(m => m.ConversationId == conversationId)
                .Where(m => conversation.SummarizedUntil == null || m.CreatedAt > conversation.SummarizedUntil)
                .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
                .Take(settings.HistoryMaxMessages)
                .ToListAsync(ct);
            recent.Reverse();
            history = HistoryWindow.Select(recent, settings.HistoryMaxMessages, settings.HistoryMaxTokens, tokenCounter);
            if (conversation.Summary is { } summary)
            {
                // Long-range memory first, then the verbatim recent turns.
                history = [new LlmMessage(LlmRole.User, $"<conversation_summary>\n{summary}\n</conversation_summary>"), .. history];
            }

            conversation.Touch(now);
        }
        else
        {
            conversation = new Conversation(tenantId, userId, content, now);
            db.Conversations.Add(conversation);
        }

        var userMessage = Message.FromUser(tenantId, conversation.Id, content);
        db.Messages.Add(userMessage);
        await db.SaveChangesAsync(ct);

        return new ChatTurn(conversation, userMessage, history, request.DocumentIds ?? []);
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamTurnAsync(ChatTurn turn, [EnumeratorCancellation] CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        yield return new ChatMetaEvent(turn.Conversation.Id, turn.UserMessage.Id, turn.Conversation.Title);

        // 1. Make the question standalone, then retrieve and build the grounded prompt.
        var standalone = await rewriter.RewriteAsync(turn.History, turn.UserMessage.Content, ct);
        RagPreparation? preparation = null;
        string? setupError = null;
        try
        {
            preparation = await rag.PrepareAsync(standalone, turn.DocumentIds, ct);
        }
        catch (AiUnavailableException ex)
        {
            setupError = ex.Message; // e.g. embedding provider down
        }

        if (preparation is null)
        {
            var failed = await SaveAssistantAsync(turn, string.Empty, MessageStatus.Failed, "Failed", standalone, [], null, null, null, stopwatch);
            yield return new ChatErrorEvent(setupError!, failed.Id);
            yield break;
        }

        // 2. Nothing relevant: answer honestly without calling the LLM.
        if (preparation.ShouldAbstain)
        {
            yield return new ChatDeltaEvent(RagService.NoAnswerMessage);
            var abstained = await SaveAssistantAsync(turn, RagService.NoAnswerMessage, MessageStatus.Completed,
                nameof(AnswerOutcome.NoRelevantSources), preparation.Question, [], null, null, null, stopwatch);
            yield return new ChatDoneEvent(abstained.Id, AnswerOutcome.NoRelevantSources, [], [], preparation.Question, null, null, null, stopwatch.ElapsedMilliseconds);
            yield break;
        }

        // 3. Stream the answer. History gives conversational continuity; sources ride only on the current turn.
        var settings = ragOptions.Value;
        var request = new LlmRequest(
            preparation.SystemPrompt,
            [.. turn.History, new LlmMessage(LlmRole.User, preparation.UserMessage)],
            settings.MaxOutputTokens,
            settings.Temperature);

        var answer = new StringBuilder();
        LlmUsage? usage = null;
        string? model = null;
        string? streamError = null;
        var interrupted = false;

        await using (var updates = Deferred(request, ct).GetAsyncEnumerator(ct))
        {
            while (true)
            {
                LlmStreamUpdate update;
                try
                {
                    if (!await updates.MoveNextAsync())
                    {
                        break;
                    }

                    update = updates.Current;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    interrupted = true;
                    break;
                }
                catch (AiUnavailableException ex)
                {
                    streamError = ex.Message;
                    break;
                }

                usage = update.Usage ?? usage;
                model = update.Model ?? model;
                if (update.TextDelta.Length > 0)
                {
                    answer.Append(update.TextDelta);
                    yield return new ChatDeltaEvent(update.TextDelta);
                }
            }
        }

        // 4. Persist whatever happened. The client may be gone, so don't use its token.
        var text = answer.ToString();
        var analysis = rag.Analyze(preparation, text);
        var citations = analysis.Citations.Select(ToMessageCitation).ToList();

        if (interrupted)
        {
            await SaveAssistantAsync(turn, text, MessageStatus.Interrupted, analysis.Outcome.ToString(), preparation.Question, citations, model, usage, preparation.PromptId, stopwatch);
            LogInterrupted(logger, turn.Conversation.Id, text.Length);
            yield break;
        }

        if (streamError is not null)
        {
            var failed = await SaveAssistantAsync(turn, text, MessageStatus.Failed, "Failed", preparation.Question, citations, model, usage, preparation.PromptId, stopwatch);
            yield return new ChatErrorEvent(streamError, failed.Id);
            yield break;
        }

        var saved = await SaveAssistantAsync(turn, text, MessageStatus.Completed, analysis.Outcome.ToString(), preparation.Question, citations, model, usage, preparation.PromptId, stopwatch);
        LogTurn(logger, turn.Conversation.Id, analysis.Outcome, preparation.Sources.Count, citations.Count, turn.History.Count, usage?.InputTokens, usage?.OutputTokens, stopwatch.ElapsedMilliseconds);

        yield return new ChatDoneEvent(saved.Id, analysis.Outcome, analysis.Citations, analysis.InvalidCitationNumbers, preparation.Question,
            model, usage?.InputTokens, usage?.OutputTokens, stopwatch.ElapsedMilliseconds);

        // After "done" has been sent: the user is not waiting on this. Uses its own token because
        // the client may disconnect as soon as it has the answer.
        await summarizer.SummarizeIfNeededAsync(turn.Conversation.Id, CancellationToken.None);
    }

    /// <summary>
    /// Starts the provider stream lazily, so even a synchronous throw from <c>StreamAsync</c> surfaces
    /// inside the guarded <c>MoveNextAsync</c> above (and becomes an error event, not a broken response).
    /// </summary>
    private async IAsyncEnumerable<LlmStreamUpdate> Deferred(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var update in llm.StreamAsync(request, ct).WithCancellation(ct))
        {
            yield return update;
        }
    }

    private async Task<Message> SaveAssistantAsync(
        ChatTurn turn, string content, MessageStatus status, string outcome, string retrievalQuery,
        IReadOnlyList<MessageCitation> citations, string? model, LlmUsage? usage, string? promptId, Stopwatch stopwatch)
    {
        var message = Message.FromAssistant(
            turn.Conversation.TenantId, turn.Conversation.Id, content, status, outcome, retrievalQuery, citations,
            model, promptId ?? ragOptions.Value.PromptId, usage?.InputTokens, usage?.OutputTokens, stopwatch.ElapsedMilliseconds);

        db.Messages.Add(message);
        turn.Conversation.Touch(time.GetUtcNow());
        await db.SaveChangesAsync(CancellationToken.None);
        return message;
    }

    private static MessageCitation ToMessageCitation(Citation c) => new()
    {
        Number = c.Number,
        DocumentId = c.DocumentId,
        FileName = c.FileName,
        PageNumber = c.PageNumber,
        Heading = c.Heading,
        Score = c.Score,
        Snippet = c.Snippet,
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Chat turn in {ConversationId}: {Outcome}, {SourceCount} sources, {CitationCount} cited, {HistoryCount} history messages, tokens in/out {InputTokens}/{OutputTokens}, {ElapsedMs} ms")]
    private static partial void LogTurn(ILogger logger, Guid conversationId, AnswerOutcome outcome, int sourceCount, int citationCount, int historyCount, long? inputTokens, long? outputTokens, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Information, Message = "Chat turn in {ConversationId} interrupted by client after {Characters} characters; partial answer saved")]
    private static partial void LogInterrupted(ILogger logger, Guid conversationId, int characters);
}
