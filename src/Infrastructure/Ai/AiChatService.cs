using System.Diagnostics;
using AISupportOps.Application.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AISupportOps.Infrastructure.Ai;

/// <summary>
/// Adapts a Microsoft.Extensions.AI <see cref="IChatClient"/> (OpenAI, Azure OpenAI, fake) to the
/// application port. Adds a hard timeout, converts provider failures into
/// <see cref="AiUnavailableException"/> (HTTP 503), and logs latency and token usage.
/// </summary>
internal sealed partial class AiChatService(
    IChatClient client,
    string modelId,
    TimeSpan timeout,
    ILogger<AiChatService> logger) : IAiChatService
{
    public string ModelId => modelId;

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        // System prompt as a dedicated system message, separate from (untrusted) user content.
        var messages = new List<ChatMessage> { new(ChatRole.System, request.SystemPrompt) };
        messages.AddRange(request.Messages.Select(m =>
            new ChatMessage(m.Role == LlmRole.User ? ChatRole.User : ChatRole.Assistant, m.Content)));

        var chatOptions = new ChatOptions
        {
            ModelId = modelId,
            MaxOutputTokens = request.MaxOutputTokens,
            Temperature = request.Temperature,
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await client.GetResponseAsync(messages, chatOptions, timeoutCts.Token);
            var usage = new LlmUsage(response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount);
            LogCompleted(logger, response.ModelId ?? modelId, usage.InputTokens, usage.OutputTokens, stopwatch.ElapsedMilliseconds);

            return new LlmResponse(response.Text, response.ModelId ?? modelId, usage, response.FinishReason?.ToString());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the caller gave up (e.g. client disconnected)
        }
        catch (OperationCanceledException ex)
        {
            LogFailed(logger, ex, modelId, stopwatch.ElapsedMilliseconds);
            throw new AiUnavailableException("The AI model did not respond in time.", ex);
        }
        catch (Exception ex) when (ex is not AiUnavailableException)
        {
            LogFailed(logger, ex, modelId, stopwatch.ElapsedMilliseconds);
            throw new AiUnavailableException("The AI model is currently unavailable.", ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "LLM call to {Model}: tokens in/out {InputTokens}/{OutputTokens} in {ElapsedMs} ms")]
    private static partial void LogCompleted(ILogger logger, string model, long? inputTokens, long? outputTokens, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Error, Message = "LLM call to {Model} failed after {ElapsedMs} ms")]
    private static partial void LogFailed(ILogger logger, Exception exception, string model, long elapsedMs);
}
