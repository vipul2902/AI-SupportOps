using AISupportOps.Domain.Common;

namespace AISupportOps.Domain.Evaluation;

/// <summary>Aggregate scores of one evaluation run. Null when the metric does not apply (e.g. no answerable cases).</summary>
public sealed class EvaluationSummary
{
    public int CaseCount { get; set; }

    /// <summary>Share of answerable cases whose expected source was among the retrieved chunks (Hit@K).</summary>
    public double? RetrievalHitRate { get; set; }

    /// <summary>Mean reciprocal rank of the expected source (1 = always first).</summary>
    public double? MeanReciprocalRank { get; set; }

    /// <summary>Share of answerable cases that were answered (not abstained).</summary>
    public double? AnswerRate { get; set; }

    /// <summary>Share of answered cases that cited the expected source.</summary>
    public double? CitationAccuracy { get; set; }

    /// <summary>Mean share of required key facts present in answers.</summary>
    public double? KeyFactCoverage { get; set; }

    /// <summary>Share of all cases where the system abstained exactly when it should have.</summary>
    public double? AbstentionAccuracy { get; set; }

    /// <summary>Mean LLM-judged groundedness (0–1). Approximate; null when the judge was not used.</summary>
    public double? Groundedness { get; set; }

    public double AverageLatencyMs { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }
}

/// <summary>
/// One execution of an evaluation dataset against the current pipeline configuration. Records the
/// prompt and models used, so results can be compared across configuration changes.
/// </summary>
public sealed class EvaluationRun : Entity, ITenantOwned
{
    private EvaluationRun()
    {
    }

    public EvaluationRun(Guid tenantId, string name, string promptId, string chatModel, string embeddingModel, bool usedJudge, Guid startedByUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        TenantId = tenantId;
        Name = name.Trim();
        PromptId = promptId;
        ChatModel = chatModel;
        EmbeddingModel = embeddingModel;
        UsedJudge = usedJudge;
        StartedByUserId = startedByUserId;
    }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string PromptId { get; private set; } = string.Empty;

    public string ChatModel { get; private set; } = string.Empty;

    public string EmbeddingModel { get; private set; } = string.Empty;

    public bool UsedJudge { get; private set; }

    public Guid StartedByUserId { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public EvaluationSummary Summary { get; private set; } = new();

    public void Complete(EvaluationSummary summary, DateTimeOffset now)
    {
        Summary = summary;
        CompletedAt = now;
    }
}

/// <summary>The outcome of one dataset case within a run.</summary>
public sealed class EvaluationResult : Entity, ITenantOwned
{
    private EvaluationResult()
    {
    }

    public EvaluationResult(Guid tenantId, Guid runId, string caseId, string question, string? expectedSource, bool shouldAbstain)
    {
        TenantId = tenantId;
        RunId = runId;
        CaseId = caseId;
        Question = question;
        ExpectedSource = expectedSource;
        ShouldAbstain = shouldAbstain;
    }

    public Guid TenantId { get; private set; }

    public Guid RunId { get; private set; }

    public string CaseId { get; private set; } = string.Empty;

    public string Question { get; private set; } = string.Empty;

    public string? ExpectedSource { get; private set; }

    public bool ShouldAbstain { get; private set; }

    public string Answer { get; set; } = string.Empty;

    public string Outcome { get; set; } = string.Empty;

    public List<string> RetrievedSources { get; set; } = [];

    public List<string> CitedSources { get; set; } = [];

    /// <summary>1-based rank of the expected source among retrieved chunks; null if not retrieved.</summary>
    public int? ExpectedSourceRank { get; set; }

    public bool? CitedExpectedSource { get; set; }

    public double? KeyFactCoverage { get; set; }

    public List<string> MissingKeyFacts { get; set; } = [];

    public bool AbstentionCorrect { get; set; }

    public double? Groundedness { get; set; }

    public string? JudgeNotes { get; set; }

    public long LatencyMs { get; set; }

    public long? InputTokens { get; set; }

    public long? OutputTokens { get; set; }
}
