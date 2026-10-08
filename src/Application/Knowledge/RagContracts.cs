using System.ComponentModel.DataAnnotations;

namespace AISupportOps.Application.Knowledge;

public sealed record AskRequest(
    [Required, StringLength(RetrievalQuery.MaxQueryLength, MinimumLength = 1)] string Question,
    IReadOnlyList<Guid>? DocumentIds = null);

public sealed record Citation(
    int Number,
    Guid DocumentId,
    string FileName,
    int? PageNumber,
    string? Heading,
    double Score,
    string Snippet);

public enum AnswerOutcome
{
    /// <summary>The model answered and cited at least one provided source.</summary>
    Answered,

    /// <summary>Nothing relevant was retrieved; the model was not called.</summary>
    NoRelevantSources,

    /// <summary>The model said the sources do not contain the answer.</summary>
    Declined,

    /// <summary>The model answered without citing any provided source: treat with suspicion.</summary>
    Uncited,
}

public sealed record RagTimings(long RetrievalMs, long GenerationMs, long TotalMs);

public sealed record AskResponse(
    string Answer,
    AnswerOutcome Outcome,
    IReadOnlyList<Citation> Citations,
    int SourcesInContext,
    IReadOnlyList<int> InvalidCitationNumbers,
    string? Model,
    string PromptId,
    long? InputTokens,
    long? OutputTokens,
    RagTimings Timings);
