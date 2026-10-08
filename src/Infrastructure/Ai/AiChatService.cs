using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
    ILogger<AiChatService> logger) : IAiChatService, IAiToolChatService
{
    public string ModelId => modelId;

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        var (messages, chatOptions) = ToProviderRequest(request);

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

    public async IAsyncEnumerable<LlmStreamUpdate> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var (messages, chatOptions) = ToProviderRequest(request);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var stopwatch = Stopwatch.StartNew();
        LlmUsage? usage = null;

        // Manual enumeration: C# forbids 'yield' inside try/catch, and provider errors must be translated.
        await using var updates = client.GetStreamingResponseAsync(messages, chatOptions, timeoutCts.Token)
            .GetAsyncEnumerator(timeoutCts.Token);
        while (true)
        {
            ChatResponseUpdate update;
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
                throw; // client disconnected
            }
            catch (Exception ex)
            {
                LogFailed(logger, ex, modelId, stopwatch.ElapsedMilliseconds);
                throw new AiUnavailableException(
                    ex is OperationCanceledException ? "The AI model did not respond in time." : "The AI model is currently unavailable.", ex);
            }

            var details = update.Contents.OfType<UsageContent>().FirstOrDefault()?.Details;
            if (details is not null)
            {
                usage = new LlmUsage(details.InputTokenCount, details.OutputTokenCount);
            }

            yield return new LlmStreamUpdate(update.Text ?? string.Empty, details is null ? null : usage, update.ModelId);
        }

        LogCompleted(logger, modelId, usage?.InputTokens, usage?.OutputTokens, stopwatch.ElapsedMilliseconds);
    }

    /// <summary>
    /// Tool calling with declaration-only tools: the provider sees name, description, and JSON schema,
    /// and can only *propose* calls. No function is bound for auto-invocation (no FunctionInvokingChatClient),
    /// so every execution goes through the application's ToolExecutor.
    /// </summary>
    public async Task<LlmToolResponse> CompleteWithToolsAsync(LlmToolRequest request, CancellationToken ct)
    {
        var messages = new List<ChatMessage> { new(ChatRole.System, request.SystemPrompt) };
        foreach (var m in request.Messages)
        {
            messages.Add(m.Role switch
            {
                AgentRole.User => new ChatMessage(ChatRole.User, m.Text ?? string.Empty),
                AgentRole.Tool => new ChatMessage(ChatRole.Tool, [new FunctionResultContent(m.ToolCallId!, m.Text)]),
                _ => new ChatMessage(ChatRole.Assistant,
                    [
                        .. string.IsNullOrEmpty(m.Text) ? Array.Empty<AIContent>() : [new TextContent(m.Text)],
                        .. (m.ToolCalls ?? []).Select(c => (AIContent)new FunctionCallContent(c.CallId, c.Name, ParseArguments(c.ArgumentsJson))),
                    ]),
            });
        }

        var chatOptions = new ChatOptions
        {
            ModelId = modelId,
            MaxOutputTokens = request.MaxOutputTokens,
            Temperature = request.Temperature,
            ToolMode = ChatToolMode.Auto,
            AllowMultipleToolCalls = true,
            Tools = [.. request.Tools.Select(t => AIFunctionFactory.CreateDeclaration(t.Name, t.Description, t.ParametersSchema))],
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await client.GetResponseAsync(messages, chatOptions, timeoutCts.Token);
            var calls = response.Messages
                .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
                .Select(c => new ToolCall(c.CallId, c.Name, JsonSerializer.Serialize(c.Arguments ?? new Dictionary<string, object?>())))
                .ToList();
            var usage = new LlmUsage(response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount);
            LogCompleted(logger, response.ModelId ?? modelId, usage.InputTokens, usage.OutputTokens, stopwatch.ElapsedMilliseconds);
            return new LlmToolResponse(response.Text, calls, response.ModelId ?? modelId, usage);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not AiUnavailableException)
        {
            LogFailed(logger, ex, modelId, stopwatch.ElapsedMilliseconds);
            throw new AiUnavailableException(
                ex is OperationCanceledException ? "The AI model did not respond in time." : "The AI model is currently unavailable.", ex);
        }
    }

    private static Dictionary<string, object?> ParseArguments(string json)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        return doc.RootElement.ValueKind == JsonValueKind.Object
            ? doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone())
            : [];
    }

    /// <summary>System prompt as a dedicated system message, separate from (untrusted) user content.</summary>
    private (List<ChatMessage> Messages, ChatOptions Options) ToProviderRequest(LlmRequest request)
    {
        var messages = new List<ChatMessage> { new(ChatRole.System, request.SystemPrompt) };
        messages.AddRange(request.Messages.Select(m =>
            new ChatMessage(m.Role == LlmRole.User ? ChatRole.User : ChatRole.Assistant, m.Content)));

        return (messages, new ChatOptions
        {
            ModelId = modelId,
            MaxOutputTokens = request.MaxOutputTokens,
            Temperature = request.Temperature,
        });
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "LLM call to {Model}: tokens in/out {InputTokens}/{OutputTokens} in {ElapsedMs} ms")]
    private static partial void LogCompleted(ILogger logger, string model, long? inputTokens, long? outputTokens, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Error, Message = "LLM call to {Model} failed after {ElapsedMs} ms")]
    private static partial void LogFailed(ILogger logger, Exception exception, string model, long elapsedMs);
}
