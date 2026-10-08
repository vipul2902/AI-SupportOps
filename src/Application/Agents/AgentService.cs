using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Text.Json;
using AISupportOps.Application.Ai;
using AISupportOps.Application.Chat;
using AISupportOps.Application.Common;
using AISupportOps.Application.Ingestion;
using AISupportOps.Domain.Agents;
using AISupportOps.Domain.Chat;
using AISupportOps.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AISupportOps.Application.Agents;

public sealed record AgentRequest(
    [Required, StringLength(4000, MinimumLength = 1)] string Message,
    Guid? ConversationId = null);

public sealed record AgentStep(string Tool, JsonElement Arguments, ToolExecutionStatus Status, string? Error, long LatencyMs);

public enum AgentOutcome
{
    Completed,

    /// <summary>Hit the iteration budget before producing a final answer.</summary>
    IterationLimit,
}

public sealed record AgentResponse(
    Guid ConversationId,
    Guid MessageId,
    Guid RunId,
    string Answer,
    AgentOutcome Outcome,
    IReadOnlyList<AgentStep> Steps,
    IReadOnlyList<string> ToolsOffered,
    int Iterations,
    long? InputTokens,
    long? OutputTokens,
    long LatencyMs);

/// <summary>
/// The agent loop. The model only proposes; this code decides. Each iteration: send conversation +
/// permitted tool declarations → if the model returns tool calls, run each through
/// <see cref="ToolExecutor"/> and append results → repeat until a plain answer or the iteration budget.
/// </summary>
public sealed partial class AgentService(
    IApplicationDbContext db,
    ICurrentUser currentUser,
    ToolRegistry registry,
    ToolExecutor executor,
    IAiToolChatService llm,
    ITokenCounter tokenCounter,
    IOptions<AgentOptions> options,
    IOptions<ConversationOptions> conversationOptions,
    TimeProvider time,
    ILogger<AgentService> logger)
{
    public const string IterationLimitMessage =
        "I wasn't able to finish this request within my step limit. Here is what I did so far; please review the steps or rephrase the request.";

    public async Task<AgentResponse> RunAsync(AgentRequest request, CancellationToken ct)
    {
        var settings = options.Value;
        using var activity = Telemetry.Source.StartActivity("agent.run");
        var stopwatch = Stopwatch.StartNew();
        var tenantId = currentUser.RequireTenantId();
        var userId = currentUser.RequireUserId();
        var message = request.Message.Trim();

        // Least privilege, step 1: decide the toolset from the caller's *current* role.
        var member = await db.TenantMemberships
            .Where(m => m.UserId == userId)
            .Select(m => new { m.Role, m.User.DisplayName, Organization = m.Tenant.Name })
            .SingleOrDefaultAsync(ct)
            ?? throw new ForbiddenException("You are no longer a member of this organization.");
        var tools = registry.PermittedFor(member.Role);

        var (conversation, history) = await LoadConversationAsync(request.ConversationId, tenantId, userId, message, ct);
        var budget = new AgentRunBudget(Guid.CreateVersion7(), conversation.Id);

        var messages = new List<AgentMessage>(history.Select(h => h.Role == LlmRole.User ? AgentMessage.User(h.Content) : AgentMessage.Assistant(h.Content)))
        {
            AgentMessage.User(message),
        };
        var systemPrompt = PromptLibrary.Render(settings.PromptId, new Dictionary<string, string>
        {
            ["organization"] = member.Organization,
            ["user_name"] = member.DisplayName,
            ["user_role"] = member.Role.ToString(),
        });
        var definitions = tools.Select(t => new ToolDefinition(t.Name, t.Description, t.ParametersSchema)).ToList();

        var steps = new List<AgentStep>();
        long inputTokens = 0, outputTokens = 0;
        string? answer = null;
        var iterations = 0;
        string? model = null;

        while (iterations < settings.MaxIterations)
        {
            iterations++;
            var response = await llm.CompleteWithToolsAsync(
                new LlmToolRequest(systemPrompt, messages, definitions, settings.MaxOutputTokens, 0.1f), ct);
            inputTokens += response.Usage.InputTokens ?? 0;
            outputTokens += response.Usage.OutputTokens ?? 0;
            model = response.Model;

            if (response.ToolCalls.Count == 0)
            {
                answer = response.Text.Trim();
                break;
            }

            messages.Add(AgentMessage.AssistantCalls(response.Text, response.ToolCalls));
            foreach (var call in response.ToolCalls)
            {
                var outcome = await executor.ExecuteAsync(call, budget, ct);
                steps.Add(new AgentStep(call.Name, ParseArguments(call.ArgumentsJson), outcome.Status, outcome.Summary, outcome.LatencyMs));
                messages.Add(AgentMessage.ToolResult(call.CallId, outcome.ResultJson));
            }
        }

        var outcomeKind = answer is null ? AgentOutcome.IterationLimit : AgentOutcome.Completed;
        answer = string.IsNullOrWhiteSpace(answer) ? IterationLimitMessage : answer;

        var userMessage = Message.FromUser(tenantId, conversation.Id, message);
        db.Messages.Add(userMessage);
        var assistant = Message.FromAssistant(tenantId, conversation.Id, answer, MessageStatus.Completed, $"Agent{outcomeKind}",
            null, [], model, settings.PromptId, inputTokens, outputTokens, stopwatch.ElapsedMilliseconds);
        db.Messages.Add(assistant);
        conversation.Touch(time.GetUtcNow());
        await db.SaveChangesAsync(CancellationToken.None);

        var failedSteps = steps.Count(s => s.Status != ToolExecutionStatus.Succeeded);
        Telemetry.RecordTokens("agent", inputTokens, outputTokens);
        activity?.SetTag("agent.run_id", budget.RunId);
        activity?.SetTag("agent.role", member.Role.ToString());
        activity?.SetTag("agent.tools_offered", tools.Count);
        activity?.SetTag("agent.iterations", iterations);
        activity?.SetTag("agent.tool_calls", steps.Count);
        activity?.SetTag("agent.outcome", outcomeKind.ToString());
        LogRun(logger, budget.RunId, member.Role, tools.Count, iterations, steps.Count, failedSteps, outcomeKind, stopwatch.ElapsedMilliseconds);
        return new AgentResponse(conversation.Id, assistant.Id, budget.RunId, answer, outcomeKind, steps,
            tools.Select(t => t.Name).ToList(), iterations, inputTokens, outputTokens, stopwatch.ElapsedMilliseconds);
    }

    private async Task<(Conversation Conversation, IReadOnlyList<LlmMessage> History)> LoadConversationAsync(
        Guid? conversationId, Guid tenantId, Guid userId, string firstMessage, CancellationToken ct)
    {
        if (conversationId is not { } id)
        {
            // Saved up front: tool executions reference it, and nothing pending should ride along with tool saves.
            var created = new Conversation(tenantId, userId, firstMessage, time.GetUtcNow());
            db.Conversations.Add(created);
            await db.SaveChangesAsync(ct);
            return (created, []);
        }

        var conversation = await db.Conversations.SingleOrDefaultAsync(c => c.Id == id && c.UserId == userId, ct)
            ?? throw new NotFoundException("Conversation not found.");
        var settings = conversationOptions.Value;
        var recent = await db.Messages.Where(m => m.ConversationId == id)
            .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
            .Take(settings.HistoryMaxMessages).ToListAsync(ct);
        recent.Reverse();
        return (conversation, HistoryWindow.Select(recent, settings.HistoryMaxMessages, settings.HistoryMaxTokens, tokenCounter));
    }

    private static JsonElement ParseArguments(string json)
    {
        try
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json).RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(new { raw = json });
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent run {RunId} (role {Role}, {ToolCount} tools offered): {Iterations} iterations, {StepCount} tool calls ({FailedSteps} not succeeded), {Outcome}, {ElapsedMs} ms")]
    private static partial void LogRun(ILogger logger, Guid runId, TenantRole role, int toolCount, int iterations, int stepCount, int failedSteps, AgentOutcome outcome, long elapsedMs);
}
