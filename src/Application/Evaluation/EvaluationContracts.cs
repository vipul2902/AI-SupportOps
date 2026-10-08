using System.ComponentModel.DataAnnotations;
using AISupportOps.Domain.Evaluation;

namespace AISupportOps.Application.Evaluation;

/// <summary>
/// One labelled test question. <see cref="ExpectedSource"/> is the document file name that should
/// be retrieved and cited; <see cref="KeyFacts"/> are short strings a correct answer must contain.
/// Unanswerable cases set <see cref="ShouldAbstain"/>: the right behaviour is "I don't know".
/// </summary>
public sealed record EvaluationCase(
    [Required, StringLength(100, MinimumLength = 1)] string Id,
    [Required, StringLength(2000, MinimumLength = 1)] string Question,
    string? ExpectedSource = null,
    IReadOnlyList<string>? KeyFacts = null,
    bool ShouldAbstain = false);

public sealed record RunEvaluationRequest(
    [Required, StringLength(100, MinimumLength = 1)] string Name,
    [Required, MinLength(1), MaxLength(200)] IReadOnlyList<EvaluationCase> Cases,
    bool UseLlmJudge = false);

public sealed record EvaluationRunSummary(
    Guid Id,
    string Name,
    string PromptId,
    string ChatModel,
    string EmbeddingModel,
    bool UsedJudge,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    EvaluationSummary Summary);

public sealed record EvaluationCaseResult(
    string CaseId,
    string Question,
    string? ExpectedSource,
    bool ShouldAbstain,
    string Answer,
    string Outcome,
    IReadOnlyList<string> RetrievedSources,
    IReadOnlyList<string> CitedSources,
    int? ExpectedSourceRank,
    bool? CitedExpectedSource,
    double? KeyFactCoverage,
    IReadOnlyList<string> MissingKeyFacts,
    bool AbstentionCorrect,
    double? Groundedness,
    string? JudgeNotes,
    long LatencyMs);

public sealed record EvaluationRunDetail(EvaluationRunSummary Run, IReadOnlyList<EvaluationCaseResult> Results);

public sealed record FeedbackRequest(bool Helpful, [MaxLength(1000)] string? Comment = null);

// ---- production metrics ----

public sealed record LatencyStats(double? AverageMs, double? P50Ms, double? P95Ms);

public sealed record DailyPoint(DateOnly Date, int Questions, int Answered, double? AverageLatencyMs);

public sealed record ToolStat(string Tool, int Calls, int Succeeded, int Denied, int Invalid, int Failed, double AverageLatencyMs);

public sealed record AiMetrics(
    DateTimeOffset Since,
    int TotalAnswers,
    IReadOnlyDictionary<string, int> Outcomes,
    double? AnswerRate,
    double? AbstentionRate,
    double? UncitedRate,
    double? AverageCitationsPerAnswer,
    double? AverageTopCitationScore,
    LatencyStats Latency,
    long InputTokens,
    long OutputTokens,
    int HelpfulFeedback,
    int UnhelpfulFeedback,
    double? Satisfaction,
    IReadOnlyList<DailyPoint> Daily,
    IReadOnlyList<ToolStat> Tools,
    EvaluationRunSummary? LatestEvaluation);
