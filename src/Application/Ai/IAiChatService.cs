namespace AISupportOps.Application.Ai;

public enum LlmRole
{
    User,
    Assistant,
}

public sealed record LlmMessage(LlmRole Role, string Content);

/// <summary>A provider-neutral chat completion request. The system prompt is passed separately from user content.</summary>
public sealed record LlmRequest(string SystemPrompt, IReadOnlyList<LlmMessage> Messages, int MaxOutputTokens, float Temperature);

public sealed record LlmUsage(long? InputTokens, long? OutputTokens);

public sealed record LlmResponse(string Text, string Model, LlmUsage Usage, string? FinishReason);

/// <summary>
/// The application's port to a chat model. Keeps prompts, retries, and provider details out of use cases,
/// and lets tests substitute a deterministic model.
/// </summary>
public interface IAiChatService
{
    string ModelId { get; }

    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct);
}

/// <summary>The AI provider failed or timed out. Mapped to 503 so clients can retry.</summary>
public sealed class AiUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
