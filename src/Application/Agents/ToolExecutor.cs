using System.Diagnostics;
using System.Text.Json;
using AISupportOps.Application.Ai;
using AISupportOps.Application.Auditing;
using AISupportOps.Application.Common;
using AISupportOps.Domain.Agents;
using AISupportOps.Domain.Auditing;
using AISupportOps.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AISupportOps.Application.Agents;

/// <summary>All registered tools. The agent only ever sees the subset permitted for the caller's role.</summary>
public sealed class ToolRegistry(IEnumerable<IAgentTool> tools)
{
    private readonly Dictionary<string, IAgentTool> _byName = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);

    public IAgentTool? Find(string name) => _byName.GetValueOrDefault(name);

    public IReadOnlyList<IAgentTool> PermittedFor(TenantRole role) =>
        _byName.Values.Where(t => role >= t.MinimumRole).OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
}

/// <summary>Per-run counters for the safety budgets.</summary>
public sealed class AgentRunBudget(Guid runId, Guid? conversationId)
{
    public Guid RunId { get; } = runId;

    public Guid? ConversationId { get; } = conversationId;

    public int ToolCalls { get; set; }

    public int WriteCalls { get; set; }
}

public sealed record ToolCallOutcome(ToolExecutionStatus Status, string ResultJson, string? Summary, long LatencyMs);

/// <summary>
/// The only path from a model-proposed tool call to real execution. In order:
/// budget → lookup → authorization (current role from the database) → argument validation →
/// execution with timeout → result shaping. Every outcome, including refusals, is persisted as a
/// <see cref="ToolExecution"/>; data changes are audited with actor type AiAgent.
/// Failures are returned to the model as structured errors so it can correct itself or explain.
/// </summary>
public sealed partial class ToolExecutor(
    ToolRegistry registry,
    IApplicationDbContext db,
    ICurrentUser currentUser,
    AuditTrail audit,
    IOptions<AgentOptions> options,
    ILogger<ToolExecutor> logger)
{
    private static readonly JsonSerializerOptions ResultJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public async Task<ToolCallOutcome> ExecuteAsync(ToolCall call, AgentRunBudget budget, CancellationToken ct)
    {
        var settings = options.Value;
        var stopwatch = Stopwatch.StartNew();
        var tool = registry.Find(call.Name);

        // 1. Budget: bounds cost and blast radius regardless of what the model asks for.
        budget.ToolCalls++;
        if (budget.ToolCalls > settings.MaxToolCallsPerRun)
        {
            return await RecordAsync(call, budget, ToolExecutionStatus.BudgetExceeded, null,
                $"Tool call limit ({settings.MaxToolCallsPerRun}) reached for this request. Summarize what was done so far.", stopwatch);
        }

        // 2. Lookup: the model may hallucinate tools.
        if (tool is null)
        {
            return await RecordAsync(call, budget, ToolExecutionStatus.UnknownTool, null, $"Tool '{call.Name}' does not exist.", stopwatch);
        }

        // 3. Authorization, evaluated now against the database (not the model, not a stale token claim).
        //    Tools above the caller's role were never offered, but a model can still name them.
        var role = await CurrentRoleAsync(ct);
        if (role is null || role < tool.MinimumRole)
        {
            LogDenied(logger, call.Name, currentUser.UserId, role);
            return await RecordAsync(call, budget, ToolExecutionStatus.Denied, null,
                $"Permission denied: '{call.Name}' requires the {tool.MinimumRole} role.", stopwatch);
        }

        if (tool.IsWrite && ++budget.WriteCalls > settings.MaxWriteCallsPerRun)
        {
            return await RecordAsync(call, budget, ToolExecutionStatus.BudgetExceeded, null,
                $"Write action limit ({settings.MaxWriteCallsPerRun}) reached for this request.", stopwatch);
        }

        // 4. Validate untrusted arguments against the tool's typed contract.
        var bindError = tool.TryBind(call.ArgumentsJson, out var arguments);
        if (bindError is not null)
        {
            return await RecordAsync(call, budget, ToolExecutionStatus.InvalidArguments, null, bindError, stopwatch);
        }

        // 5. Execute through the same application services as the REST API.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(settings.ToolTimeout);
        var previousActor = audit.ActorType;
        audit.ActorType = AuditActorType.AiAgent;
        try
        {
            var result = await tool.ExecuteAsync(arguments!, timeout.Token);
            return result.Success
                ? await RecordAsync(call, budget, ToolExecutionStatus.Succeeded, Serialize(result.Data), null, stopwatch)
                : await RecordAsync(call, budget, ToolExecutionStatus.Rejected, null, result.Error, stopwatch);
        }
        catch (AppException ex)
        {
            // Expected business outcomes (not found, invalid transition, conflict): safe to show the model.
            return await RecordAsync(call, budget, ToolExecutionStatus.Rejected, null, ex.Message, stopwatch);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return await RecordAsync(call, budget, ToolExecutionStatus.Failed, null, "The tool timed out.", stopwatch);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never leak internals into the prompt; log them instead.
            LogToolCrashed(logger, ex, call.Name);
            return await RecordAsync(call, budget, ToolExecutionStatus.Failed, null, "The tool failed unexpectedly.", stopwatch);
        }
        finally
        {
            audit.ActorType = previousActor;
        }
    }

    private async Task<TenantRole?> CurrentRoleAsync(CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        return await db.TenantMemberships.Where(m => m.UserId == userId).Select(m => (TenantRole?)m.Role).SingleOrDefaultAsync(ct);
    }

    private async Task<ToolCallOutcome> RecordAsync(
        ToolCall call, AgentRunBudget budget, ToolExecutionStatus status, string? resultJson, string? error, Stopwatch stopwatch)
    {
        var latency = stopwatch.ElapsedMilliseconds;

        // A failed business operation may have left pending, unsaved changes (e.g. a half-built ticket).
        // Discard only those, so they are not committed with this record; saved entities stay tracked.
        if (status != ToolExecutionStatus.Succeeded)
        {
            foreach (var entry in db.ChangeTracker.Entries()
                         .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList())
            {
                entry.State = EntityState.Detached;
            }
        }

        db.ToolExecutions.Add(new ToolExecution(
            currentUser.RequireTenantId(), currentUser.RequireUserId(), budget.RunId, budget.ConversationId,
            call.Name, call.ArgumentsJson, status, resultJson, error, latency));
        await db.SaveChangesAsync(CancellationToken.None);

        LogExecuted(logger, call.Name, status, latency);
        var forModel = status == ToolExecutionStatus.Succeeded
            ? Truncate(resultJson!)
            : JsonSerializer.Serialize(new { error }, ResultJson);
        return new ToolCallOutcome(status, forModel, error, latency);
    }

    private static string Serialize(object? data) => JsonSerializer.Serialize(data, ResultJson);

    private string Truncate(string json) =>
        json.Length <= options.Value.MaxToolResultChars
            ? json
            : JsonSerializer.Serialize(new { truncated = true, partial = json[..options.Value.MaxToolResultChars] }, ResultJson);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent tool {Tool}: {Status} in {ElapsedMs} ms")]
    private static partial void LogExecuted(ILogger logger, string tool, ToolExecutionStatus status, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent tool {Tool} denied for user {UserId} with role {Role}")]
    private static partial void LogDenied(ILogger logger, string tool, Guid? userId, TenantRole? role);

    [LoggerMessage(Level = LogLevel.Error, Message = "Agent tool {Tool} crashed")]
    private static partial void LogToolCrashed(ILogger logger, Exception exception, string tool);
}
