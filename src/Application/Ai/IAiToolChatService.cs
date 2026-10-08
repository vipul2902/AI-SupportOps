using System.Text.Json;

namespace AISupportOps.Application.Ai;

/// <summary>A tool the model may propose calling. Declaration only: the model never executes anything.</summary>
public sealed record ToolDefinition(string Name, string Description, JsonElement ParametersSchema);

/// <summary>A call proposed by the model. Arguments are untrusted JSON until validated.</summary>
public sealed record ToolCall(string CallId, string Name, string ArgumentsJson);

public enum AgentRole
{
    User,
    Assistant,
    Tool,
}

/// <summary>Provider-neutral message for tool-calling conversations.</summary>
public sealed record AgentMessage(AgentRole Role, string? Text, IReadOnlyList<ToolCall>? ToolCalls = null, string? ToolCallId = null)
{
    public static AgentMessage User(string text) => new(AgentRole.User, text);

    public static AgentMessage AssistantCalls(string? text, IReadOnlyList<ToolCall> calls) => new(AgentRole.Assistant, text, calls);

    public static AgentMessage Assistant(string text) => new(AgentRole.Assistant, text);

    public static AgentMessage ToolResult(string callId, string resultJson) => new(AgentRole.Tool, resultJson, ToolCallId: callId);
}

public sealed record LlmToolRequest(
    string SystemPrompt,
    IReadOnlyList<AgentMessage> Messages,
    IReadOnlyList<ToolDefinition> Tools,
    int MaxOutputTokens,
    float Temperature);

public sealed record LlmToolResponse(string Text, IReadOnlyList<ToolCall> ToolCalls, string Model, LlmUsage Usage);

/// <summary>Chat completion with tool (function) calling. Separate from <see cref="IAiChatService"/> to keep each port small.</summary>
public interface IAiToolChatService
{
    Task<LlmToolResponse> CompleteWithToolsAsync(LlmToolRequest request, CancellationToken ct);
}
