using AISupportOps.Domain.Common;

namespace AISupportOps.Domain.Agents;

public enum ToolExecutionStatus
{
    Succeeded,

    /// <summary>The model asked for a tool that does not exist.</summary>
    UnknownTool,

    /// <summary>The caller's role does not permit this tool.</summary>
    Denied,

    /// <summary>Arguments failed schema or validation checks.</summary>
    InvalidArguments,

    /// <summary>A business rule rejected the action (e.g. invalid status transition).</summary>
    Rejected,

    /// <summary>Unexpected failure or timeout.</summary>
    Failed,

    /// <summary>Per-run safety budget exceeded (too many calls or write actions).</summary>
    BudgetExceeded,
}

/// <summary>
/// One tool call proposed by the AI agent and how the system handled it. Every call is recorded,
/// including denied and invalid ones: those are the most interesting rows for security review.
/// </summary>
public sealed class ToolExecution : Entity, ITenantOwned
{
    public const int MaxPayloadLength = 8000;

    private ToolExecution()
    {
    }

    public ToolExecution(
        Guid tenantId, Guid userId, Guid runId, Guid? conversationId, string toolName, string argumentsJson,
        ToolExecutionStatus status, string? resultJson, string? error, long latencyMs)
    {
        TenantId = tenantId;
        UserId = userId;
        RunId = runId;
        ConversationId = conversationId;
        ToolName = toolName.Length > 100 ? toolName[..100] : toolName;
        ArgumentsJson = Truncate(argumentsJson)!;
        Status = status;
        ResultJson = Truncate(resultJson);
        Error = Truncate(error);
        LatencyMs = latencyMs;
    }

    public Guid TenantId { get; private set; }

    /// <summary>The user the agent acted for. Authorization is evaluated as this user.</summary>
    public Guid UserId { get; private set; }

    /// <summary>Groups all calls of one agent run.</summary>
    public Guid RunId { get; private set; }

    public Guid? ConversationId { get; private set; }

    public string ToolName { get; private set; } = string.Empty;

    public string ArgumentsJson { get; private set; } = string.Empty;

    public ToolExecutionStatus Status { get; private set; }

    public string? ResultJson { get; private set; }

    public string? Error { get; private set; }

    public long LatencyMs { get; private set; }

    private static string? Truncate(string? value) =>
        value is { Length: > MaxPayloadLength } ? value[..MaxPayloadLength] + "…" : value;
}
