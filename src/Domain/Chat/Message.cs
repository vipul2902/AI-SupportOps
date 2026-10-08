using AISupportOps.Domain.Common;

namespace AISupportOps.Domain.Chat;

public enum MessageRole
{
    User,
    Assistant,
}

public enum MessageStatus
{
    Completed,

    /// <summary>The client disconnected mid-stream; the partial answer is kept.</summary>
    Interrupted,

    /// <summary>The AI provider failed; no usable answer.</summary>
    Failed,
}

/// <summary>A source cited by an assistant message. Stored as JSON on the message (a snapshot, not a live link).</summary>
public sealed class MessageCitation
{
    public int Number { get; set; }

    public Guid DocumentId { get; set; }

    public string FileName { get; set; } = string.Empty;

    public int? PageNumber { get; set; }

    public string? Heading { get; set; }

    public double Score { get; set; }

    public string Snippet { get; set; } = string.Empty;
}

/// <summary>
/// One turn in a conversation. Assistant messages also record how the answer was produced
/// (model, prompt version, tokens, latency, outcome): the raw data for AI evaluation and cost tracking.
/// </summary>
public sealed class Message : Entity, ITenantOwned
{
    private Message()
    {
    }

    private Message(Guid tenantId, Guid conversationId, MessageRole role, string content)
    {
        TenantId = tenantId;
        ConversationId = conversationId;
        Role = role;
        Content = content;
        Status = MessageStatus.Completed;
    }

    public Guid TenantId { get; private set; }

    public Guid ConversationId { get; private set; }

    public MessageRole Role { get; private set; }

    public string Content { get; private set; } = string.Empty;

    public MessageStatus Status { get; private set; }

    /// <summary>Standalone question actually used for retrieval (after follow-up rewriting).</summary>
    public string? RetrievalQuery { get; private set; }

    public string? Outcome { get; private set; }

    public List<MessageCitation> Citations { get; private set; } = [];

    public string? Model { get; private set; }

    public string? PromptId { get; private set; }

    public long? InputTokens { get; private set; }

    public long? OutputTokens { get; private set; }

    public long? LatencyMs { get; private set; }

    public static Message FromUser(Guid tenantId, Guid conversationId, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        return new Message(tenantId, conversationId, MessageRole.User, content);
    }

    public static Message FromAssistant(
        Guid tenantId,
        Guid conversationId,
        string content,
        MessageStatus status,
        string outcome,
        string? retrievalQuery,
        IEnumerable<MessageCitation> citations,
        string? model,
        string promptId,
        long? inputTokens,
        long? outputTokens,
        long latencyMs) =>
        new(tenantId, conversationId, MessageRole.Assistant, content)
        {
            Status = status,
            Outcome = outcome,
            RetrievalQuery = retrievalQuery,
            Citations = citations.ToList(),
            Model = model,
            PromptId = promptId,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            LatencyMs = latencyMs,
        };
}
