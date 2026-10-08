using System.Diagnostics;
using AISupportOps.Application.Ai;
using AISupportOps.Application.Common;
using AISupportOps.Application.Knowledge;
using AISupportOps.Domain.Evaluation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AISupportOps.Application.Evaluation;

/// <summary>
/// Offline evaluation: runs labelled cases through the real RAG pipeline (same retrieval, threshold,
/// prompt, and model as production) and scores each case with deterministic checks first, and an
/// optional LLM judge second. Runs synchronously; fine for small datasets (tens of cases). Large
/// datasets would move to the background worker.
/// </summary>
public sealed partial class EvaluationService(
    IApplicationDbContext db,
    ICurrentUser currentUser,
    IRagService rag,
    IAiChatService llm,
    IEmbeddingService embeddings,
    GroundednessJudge judge,
    IOptions<RagOptions> ragOptions,
    TimeProvider time,
    ILogger<EvaluationService> logger)
{
    public async Task<EvaluationRunDetail> RunAsync(RunEvaluationRequest request, CancellationToken ct)
    {
        var tenantId = currentUser.RequireTenantId();
        var duplicateIds = request.Cases.GroupBy(c => c.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicateIds.Count > 0)
        {
            throw new BusinessRuleException($"Case ids must be unique: {string.Join(", ", duplicateIds)}.");
        }

        if (request.Cases.FirstOrDefault(c => !c.ShouldAbstain && string.IsNullOrWhiteSpace(c.ExpectedSource)) is { } bad)
        {
            throw new BusinessRuleException($"Case '{bad.Id}' must set expectedSource or shouldAbstain.");
        }

        var run = new EvaluationRun(tenantId, request.Name, ragOptions.Value.PromptId, llm.ModelId, embeddings.ModelId, request.UseLlmJudge, currentUser.RequireUserId());
        db.EvaluationRuns.Add(run);
        await db.SaveChangesAsync(ct);

        var results = new List<EvaluationResult>();
        foreach (var testCase in request.Cases)
        {
            results.Add(await RunCaseAsync(run, testCase, request.UseLlmJudge, ct));
        }

        run.Complete(Summarize(results), time.GetUtcNow());
        db.EvaluationResults.AddRange(results);
        await db.SaveChangesAsync(ct);

        LogRun(logger, run.Id, results.Count, run.Summary.RetrievalHitRate, run.Summary.AbstentionAccuracy, run.Summary.CitationAccuracy);
        return new EvaluationRunDetail(ToSummary(run), results.Select(ToCaseResult).ToList());
    }

    private async Task<EvaluationResult> RunCaseAsync(EvaluationRun run, EvaluationCase testCase, bool useJudge, CancellationToken ct)
    {
        var result = new EvaluationResult(run.TenantId, run.Id, testCase.Id, testCase.Question, testCase.ExpectedSource, testCase.ShouldAbstain);
        var stopwatch = Stopwatch.StartNew();
        var settings = ragOptions.Value;

        var preparation = await rag.PrepareAsync(testCase.Question, [], ct);
        result.RetrievedSources = preparation.Sources.Select(s => s.Chunk.FileName).Distinct().ToList();
        if (testCase.ExpectedSource is { } expected)
        {
            var index = preparation.Sources.ToList().FindIndex(s => string.Equals(s.Chunk.FileName, expected, StringComparison.OrdinalIgnoreCase));
            result.ExpectedSourceRank = index < 0 ? null : index + 1;
        }

        AnswerOutcome outcome;
        if (preparation.ShouldAbstain)
        {
            result.Answer = RagService.NoAnswerMessage;
            outcome = AnswerOutcome.NoRelevantSources;
        }
        else
        {
            var response = await llm.CompleteAsync(
                new LlmRequest(preparation.SystemPrompt, [new LlmMessage(LlmRole.User, preparation.UserMessage)], settings.MaxOutputTokens, settings.Temperature), ct);
            var analysis = rag.Analyze(preparation, response.Text);
            outcome = analysis.Outcome;
            result.Answer = response.Text;
            result.CitedSources = analysis.Citations.Select(c => c.FileName).Distinct().ToList();
            result.InputTokens = response.Usage.InputTokens;
            result.OutputTokens = response.Usage.OutputTokens;

            if (useJudge)
            {
                var verdict = await judge.JudgeAsync(preparation, response.Text, ct);
                result.Groundedness = verdict.Score;
                result.JudgeNotes = verdict.Notes;
            }
        }

        result.Outcome = outcome.ToString();
        var abstained = outcome is AnswerOutcome.NoRelevantSources or AnswerOutcome.Declined;
        result.AbstentionCorrect = abstained == testCase.ShouldAbstain;

        if (!testCase.ShouldAbstain && !abstained)
        {
            result.CitedExpectedSource = result.CitedSources.Contains(testCase.ExpectedSource!, StringComparer.OrdinalIgnoreCase);
            if (testCase.KeyFacts is { Count: > 0 } facts)
            {
                result.MissingKeyFacts = facts.Where(f => !result.Answer.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
                result.KeyFactCoverage = Math.Round(1 - ((double)result.MissingKeyFacts.Count / facts.Count), 3);
            }
        }

        result.LatencyMs = stopwatch.ElapsedMilliseconds;
        return result;
    }

    public static EvaluationSummary Summarize(IReadOnlyList<EvaluationResult> results)
    {
        var answerable = results.Where(r => !r.ShouldAbstain).ToList();
        var answered = answerable.Where(r => r.CitedExpectedSource is not null).ToList();

        return new EvaluationSummary
        {
            CaseCount = results.Count,
            RetrievalHitRate = Mean(answerable, r => r.ExpectedSourceRank is null ? 0 : 1),
            MeanReciprocalRank = Mean(answerable, r => r.ExpectedSourceRank is { } rank ? 1.0 / rank : 0),
            AnswerRate = Mean(answerable, r => r.CitedExpectedSource is null ? 0 : 1),
            CitationAccuracy = Mean(answered, r => r.CitedExpectedSource == true ? 1 : 0),
            KeyFactCoverage = Mean(answered.Where(r => r.KeyFactCoverage is not null).ToList(), r => r.KeyFactCoverage!.Value),
            AbstentionAccuracy = Mean(results, r => r.AbstentionCorrect ? 1 : 0),
            Groundedness = Mean(results.Where(r => r.Groundedness is not null).ToList(), r => r.Groundedness!.Value),
            AverageLatencyMs = results.Count == 0 ? 0 : Math.Round(results.Average(r => r.LatencyMs), 1),
            InputTokens = results.Sum(r => r.InputTokens ?? 0),
            OutputTokens = results.Sum(r => r.OutputTokens ?? 0),
        };
    }

    public async Task<IReadOnlyList<EvaluationRunSummary>> ListRunsAsync(CancellationToken ct) =>
        (await db.EvaluationRuns.AsNoTracking().OrderByDescending(r => r.CreatedAt).Take(50).ToListAsync(ct)).Select(ToSummary).ToList();

    public async Task<EvaluationRunDetail> GetRunAsync(Guid id, CancellationToken ct)
    {
        var run = await db.EvaluationRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new NotFoundException("Evaluation run not found.");
        var results = await db.EvaluationResults.AsNoTracking().Where(r => r.RunId == id).OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToListAsync(ct);
        return new EvaluationRunDetail(ToSummary(run), results.Select(ToCaseResult).ToList());
    }

    public static EvaluationRunSummary ToSummary(EvaluationRun r) =>
        new(r.Id, r.Name, r.PromptId, r.ChatModel, r.EmbeddingModel, r.UsedJudge, r.CreatedAt, r.CompletedAt, r.Summary);

    private static EvaluationCaseResult ToCaseResult(EvaluationResult r) =>
        new(r.CaseId, r.Question, r.ExpectedSource, r.ShouldAbstain, r.Answer, r.Outcome, r.RetrievedSources, r.CitedSources,
            r.ExpectedSourceRank, r.CitedExpectedSource, r.KeyFactCoverage, r.MissingKeyFacts, r.AbstentionCorrect, r.Groundedness, r.JudgeNotes, r.LatencyMs);

    private static double? Mean<T>(IReadOnlyCollection<T> items, Func<T, double> selector) =>
        items.Count == 0 ? null : Math.Round(items.Average(selector), 3);

    [LoggerMessage(Level = LogLevel.Information, Message = "Evaluation run {RunId}: {CaseCount} cases, hit rate {HitRate}, abstention accuracy {AbstentionAccuracy}, citation accuracy {CitationAccuracy}")]
    private static partial void LogRun(ILogger logger, Guid runId, int caseCount, double? hitRate, double? abstentionAccuracy, double? citationAccuracy);
}

