using System.Diagnostics;
using AISupportOps.Application.Ai;
using AISupportOps.Application.Common;
using AISupportOps.Application.Ingestion;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AISupportOps.Application.Knowledge;

public interface IRagService
{
    Task<AskResponse> AskAsync(AskRequest request, CancellationToken ct);
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
        var tenantId = currentUser.RequireTenantId();
        var question = KnowledgeSearchService.Preprocess(request.Question);
        if (question.Length == 0)
        {
            throw new BusinessRuleException("Question must contain text.");
        }

        var total = Stopwatch.StartNew();

        // 1. Retrieve and keep only relevant chunks.
        var retrievalTimer = Stopwatch.StartNew();
        var retrieved = await retrieval.SearchAsync(
            new RetrievalQuery(question, settings.TopK, request.DocumentIds ?? [], settings.MinScore), ct);
        var retrievalMs = retrievalTimer.ElapsedMilliseconds;

        if (retrieved.Count == 0)
        {
            LogAbstained(logger, tenantId, settings.MinScore);
            return new AskResponse(NoAnswerMessage, AnswerOutcome.NoRelevantSources, [], 0, [], null, settings.PromptId,
                null, null, new RagTimings(retrievalMs, 0, total.ElapsedMilliseconds));
        }

        // 2. Budgeted, numbered, escaped context.
        var context = new RagContextBuilder(tokenCounter).Build(retrieved, settings.MaxContextTokens);
        var organization = await db.Tenants.Where(t => t.Id == tenantId).Select(t => t.Name).SingleAsync(ct);
        var systemPrompt = PromptLibrary.Render(settings.PromptId, new Dictionary<string, string> { ["organization"] = organization });
        var userMessage = $"""
            Sources:
            {context.Text}

            <question>
            {RagContextBuilder.EscapeContent(question)}
            </question>
            """;

        // 3. Generate.
        var generationTimer = Stopwatch.StartNew();
        var response = await llm.CompleteAsync(
            new LlmRequest(systemPrompt, [new LlmMessage(LlmRole.User, userMessage)], settings.MaxOutputTokens, settings.Temperature), ct);
        var generationMs = generationTimer.ElapsedMilliseconds;

        // 4. Verify citations against what was actually provided.
        var analysis = CitationParser.Analyze(response.Text, context.Sources);
        var outcome = Classify(response.Text, analysis);
        var citations = analysis.Cited.Select(s => new Citation(
            s.Number, s.Chunk.DocumentId, s.Chunk.FileName, s.Chunk.PageNumber, s.Chunk.Heading, s.Chunk.Score, Snippet(s.Chunk.Content))).ToList();

        LogAnswered(logger, tenantId, outcome, context.Sources.Count, citations.Count, analysis.InvalidNumbers.Count,
            response.Usage.InputTokens, response.Usage.OutputTokens, retrievalMs, generationMs);

        return new AskResponse(response.Text, outcome, citations, context.Sources.Count, analysis.InvalidNumbers, response.Model,
            settings.PromptId, response.Usage.InputTokens, response.Usage.OutputTokens,
            new RagTimings(retrievalMs, generationMs, total.ElapsedMilliseconds));
    }

    private static AnswerOutcome Classify(string answer, CitationAnalysis analysis)
    {
        if (answer.Contains(NoAnswerMessage, StringComparison.OrdinalIgnoreCase))
        {
            return AnswerOutcome.Declined;
        }

        return analysis.Cited.Count > 0 ? AnswerOutcome.Answered : AnswerOutcome.Uncited;
    }

    private static string Snippet(string content) =>
        content.Length <= SnippetLength ? content : content[..SnippetLength].TrimEnd() + "…";

    [LoggerMessage(Level = LogLevel.Information, Message = "RAG abstained for tenant {TenantId}: no chunks above score {MinScore}")]
    private static partial void LogAbstained(ILogger logger, Guid tenantId, double minScore);

    [LoggerMessage(Level = LogLevel.Information, Message = "RAG answer for tenant {TenantId}: {Outcome}, {SourceCount} sources, {CitationCount} cited, {InvalidCitations} invalid citations, tokens in/out {InputTokens}/{OutputTokens}, retrieval {RetrievalMs} ms, generation {GenerationMs} ms")]
    private static partial void LogAnswered(ILogger logger, Guid tenantId, AnswerOutcome outcome, int sourceCount, int citationCount, int invalidCitations, long? inputTokens, long? outputTokens, long retrievalMs, long generationMs);
}
