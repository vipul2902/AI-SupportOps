using System.ComponentModel.DataAnnotations;
using AISupportOps.Application.Knowledge;
using AISupportOps.Domain.Chat;

namespace AISupportOps.Application.Chat;

public sealed record ChatRequest(
    [Required, StringLength(4000, MinimumLength = 1)] string Message,
    Guid? ConversationId = null,
    IReadOnlyList<Guid>? DocumentIds = null);

/// <summary>Events of one streamed assistant turn, in order: meta → delta* → (done | error).</summary>
public abstract record ChatStreamEvent
{
    /// <summary>
    /// The SSE "event:" field. A method, not a property, so it is never serialized into the payload
    /// ([JsonIgnore] on an abstract property is not inherited by the overriding properties).
    /// </summary>
    public abstract string GetEventType();
}

public sealed record ChatMetaEvent(Guid ConversationId, Guid UserMessageId, string ConversationTitle) : ChatStreamEvent
{
    public override string GetEventType() => "meta";
}

public sealed record ChatDeltaEvent(string Text) : ChatStreamEvent
{
    public override string GetEventType() => "delta";
}

public sealed record ChatDoneEvent(
    Guid MessageId,
    AnswerOutcome Outcome,
    IReadOnlyList<Citation> Citations,
    IReadOnlyList<int> InvalidCitationNumbers,
    string RetrievalQuery,
    string? Model,
    long? InputTokens,
    long? OutputTokens,
    long LatencyMs) : ChatStreamEvent
{
    public override string GetEventType() => "done";
}

public sealed record ChatErrorEvent(string Message, Guid? MessageId) : ChatStreamEvent
{
    public override string GetEventType() => "error";
}

public sealed record ConversationSummary(Guid Id, string Title, DateTimeOffset CreatedAt, DateTimeOffset LastMessageAt);

public sealed record MessageResponse(
    Guid Id,
    MessageRole Role,
    string Content,
    MessageStatus Status,
    string? Outcome,
    IReadOnlyList<MessageCitation> Citations,
    DateTimeOffset CreatedAt);

public sealed record ConversationDetail(Guid Id, string Title, DateTimeOffset CreatedAt, string? Summary, IReadOnlyList<MessageResponse> Messages);

public sealed record RenameConversationRequest([Required, StringLength(Conversation.TitleMaxLength, MinimumLength = 1)] string Title);
