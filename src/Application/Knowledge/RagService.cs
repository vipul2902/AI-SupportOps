using System.Diagnostics;
using AISupportOps.Application.Ai;
using AISupportOps.Application.Common;
using AISupportOps.Application.Ingestion;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AISupportOps.Application.Knowledge;

/// <summary>Everything needed to call the LLM for one grounded answer. Empty <see cref="Sources"/> means abstain.</summary>
public sealed record RagPreparation(
    string Question,
    IReadOnlyList<ContextSource> Sources,
    string SystemPrompt,
    string UserMessage,
    string PromptId,
    long RetrievalMs)
{
    public bool ShouldAbstain => Sources.Count == 0;
}

public sealed record RagAnalysis(AnswerOutcome Outcome, IReadOnlyList<Citation> Citations, IReadOnlyList<int> InvalidCitationNumbers);

public interface IRagService
{
    /// <summary>Single-shot question answering.</summary>
    Task<AskResponse> AskAsync(AskRequest request, CancellationToken ct);

    /// <summary>Retrieve, threshold, and build the prompt. Shared by single-shot and streaming chat.</summary>
    Task<RagPreparation> PrepareAsync(string question, IReadOnlyList<Guid> documentIds, CancellationToken ct);

    /// <summary>Verify citations in a finished answer and classify it.</summary>
    RagAnalysis Analyze(RagPreparation preparation, string answer);
}

/// <summary>
/// Retrieval-Augmented Generation: retrieve → threshold → budgeted context → LLM → verify citations.
/// Abstains (without calling the LLM) when nothing relevant is retrieved: cheaper, faster, and
/// removes the main opportunity to hallucinate.
/// </summary>
public sealed partial class RagService(
    IRetrievalService retrieval,
    IAiChatService llm,
    ITokenCounter tokenCounter,
    IApplicationDbContext db,
    ICurrentUser currentUser,
    IOptions<RagOptions> options,
    ILogger<RagService> logger) : IRagService
{
    public const string NoAnswerMessage = "I couldn't find this in the knowledge base.";
    private const int SnippetLength = 240;

    public async Task<AskResponse> AskAsync(AskRequest request, CancellationToken ct)
    {
        var settings = options.Value;
        using var activity = Telemetry.Source.StartActivity("rag.ask");
        var total = Stopwatch.StartNew();
        var preparation = await PrepareAsync(request.Question, request.DocumentIds ?? [], ct);

        if (preparation.ShouldAbstain)
        {
            Telemetry.Answers.Add(1, new("outcome", nameof(AnswerOutcome.NoRelevantSources)), new("channel", "ask"));
            activity?.SetTag("rag.outcome", nameof(AnswerOutcome.NoRelevantSources));
            return new AskResponse(NoAnswerMessage, AnswerOutcome.NoRelevantSources, [], 0, [], null, preparation.PromptId,
                null, null, new RagTimings(preparation.RetrievalMs, 0, total.ElapsedMilliseconds));
        }

        var generationTimer = Stopwatch.StartNew();
        var response = await llm.CompleteAsync(
            new LlmRequest(preparation.SystemPrompt, [new LlmMessage(LlmRole.User, preparation.UserMessage)], settings.MaxOutputTokens, settings.Temperature), ct);
        var generationMs = generationTimer.ElapsedMilliseconds;
        Telemetry.GenerationDuration.Record(generationMs, new KeyValuePair<string, object?>("channel", "ask"));
        Telemetry.RecordTokens("rag", response.Usage.InputTokens, response.Usage.OutputTokens);

        var analysis = Analyze(preparation, response.Text);
        Telemetry.Answers.Add(1, new("outcome", analysis.Outcome.ToString()), new("channel", "ask"));
        activity?.SetTag("rag.outcome", analysis.Outcome.ToString());
        activity?.SetTag("rag.citations", analysis.Citations.Count);
        activity?.SetTag("rag.invalid_citations", analysis.InvalidCitationNumbers.Count);
        var tenantId = currentUser.RequireTenantId();
        LogAnswered(logger, tenantId, analysis.Outcome, preparation.Sources.Count, analysis.Citations.Count,
            analysis.InvalidCitationNumbers.Count, response.Usage.InputTokens, response.Usage.OutputTokens, preparation.RetrievalMs, generationMs);

        return new AskResponse(response.Text, analysis.Outcome, analysis.Citations, preparation.Sources.Count, analysis.InvalidCitationNumbers,
            response.Model, preparation.PromptId, response.Usage.InputTokens, response.Usage.OutputTokens,
            new RagTimings(preparation.RetrievalMs, generationMs, total.ElapsedMilliseconds));
    }

    public async Task<RagPreparation> PrepareAsync(string question, IReadOnlyList<Guid> documentIds, CancellationToken ct)
    {
        var settings = options.Value;
        var tenantId = currentUser.RequireTenantId();
        var cleaned = KnowledgeSearchService.Preprocess(question);
        if (cleaned.Length == 0)
        {
            throw new BusinessRuleException("Question must contain text.");
        }

        // 1. Retrieve and keep only relevant chunks.
        using var activity = Telemetry.Source.StartActivity("rag.prepare");
        activity?.SetTag("rag.top_k", settings.TopK);
        activity?.SetTag("rag.min_score", settings.MinScore);
        activity?.SetTag("rag.prompt_id", settings.PromptId);
        var retrievalTimer = Stopwatch.StartNew();
        var retrieved = await retrieval.SearchAsync(new RetrievalQuery(cleaned, settings.TopK, documentIds, settings.MinScore), ct);
        var retrievalMs = retrievalTimer.ElapsedMilliseconds;
        Telemetry.RetrievalDuration.Record(retrievalMs);
        activity?.SetTag("rag.retrieved", retrieved.Count);
        activity?.SetTag("rag.top_score", retrieved.Count > 0 ? retrieved[0].Score : null);

        if (retrieved.Count == 0)
        {
            activity?.SetTag("rag.abstained", true);
            LogAbstained(logger, tenantId, settings.MinScore);
            return new RagPreparation(cleaned, [], string.Empty, string.Empty, settings.PromptId, retrievalMs);
        }

        // 2. Budgeted, numbered, escaped context + versioned system prompt.
        var context = new RagContextBuilder(tokenCounter).Build(retrieved, settings.MaxContextTokens);
        activity?.SetTag("rag.sources_in_context", context.Sources.Count);
        activity?.SetTag("rag.context_tokens", context.TokenCount);
        var organization = await db.Tenants.Where(t => t.Id == tenantId).Select(t => t.Name).SingleAsync(ct);
        var systemPrompt = PromptLibrary.Render(settings.PromptId, new Dictionary<string, string> { ["organization"] = organization });
        var userMessage = $"""
            Sources:
            {context.Text}

            <question>
            {RagContextBuilder.EscapeContent(cleaned)}
            </question>
            """;

        return new RagPreparation(cleaned, context.Sources, systemPrompt, userMessage, settings.PromptId, retrievalMs);
    }

    public RagAnalysis Analyze(RagPreparation preparation, string answer)
    {
        var analysis = CitationParser.Analyze(answer, preparation.Sources);
        var citations = analysis.Cited.Select(s => new Citation(
            s.Number, s.Chunk.DocumentId, s.Chunk.FileName, s.Chunk.PageNumber, s.Chunk.Heading, s.Chunk.Score, Snippet(s.Chunk.Content))).ToList();

        var outcome = answer.Contains(NoAnswerMessage, StringComparison.OrdinalIgnoreCase) ? AnswerOutcome.Declined
            : citations.Count > 0 ? AnswerOutcome.Answered
            : AnswerOutcome.Uncited;

        return new RagAnalysis(outcome, citations, analysis.InvalidNumbers);
    }

    private static string Snippet(string content) =>
        content.Length <= SnippetLength ? content : content[..SnippetLength].TrimEnd() + "…";

    [LoggerMessage(Level = LogLevel.Information, Message = "RAG abstained for tenant {TenantId}: no chunks above score {MinScore}")]
    private static partial void LogAbstained(ILogger logger, Guid tenantId, double minScore);

    [LoggerMessage(Level = LogLevel.Information, Message = "RAG answer for tenant {TenantId}: {Outcome}, {SourceCount} sources, {CitationCount} cited, {InvalidCitations} invalid citations, tokens in/out {InputTokens}/{OutputTokens}, retrieval {RetrievalMs} ms, generation {GenerationMs} ms")]
    private static partial void LogAnswered(ILogger logger, Guid tenantId, AnswerOutcome outcome, int sourceCount, int citationCount, int invalidCitations, long? inputTokens, long? outputTokens, long retrievalMs, long generationMs);
}
