using AISupportOps.Application.Evaluation;
using AISupportOps.Domain.Evaluation;
using AISupportOps.Infrastructure.Ai;

namespace AISupportOps.UnitTests.Evaluation;

public class EvaluationScoringTests
{
    private static EvaluationResult Result(string id, bool shouldAbstain, int? rank, bool? cited, double? facts, bool abstentionOk, double? grounded = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), id, "q", shouldAbstain ? null : "doc.md", shouldAbstain)
        {
            ExpectedSourceRank = rank,
            CitedExpectedSource = cited,
            KeyFactCoverage = facts,
            AbstentionCorrect = abstentionOk,
            Groundedness = grounded,
            LatencyMs = 100,
        };

    [Fact]
    public void Summary_computes_retrieval_generation_and_abstention_metrics()
    {
        var summary = EvaluationService.Summarize(
        [
            Result("a", false, rank: 1, cited: true, facts: 1.0, abstentionOk: true, grounded: 1.0),
            Result("b", false, rank: 2, cited: false, facts: 0.5, abstentionOk: true, grounded: 0.5),
            Result("c", false, rank: null, cited: null, facts: null, abstentionOk: false), // wrongly abstained, not retrieved
            Result("d", true, rank: null, cited: null, facts: null, abstentionOk: true),
        ]);

        Assert.Equal(4, summary.CaseCount);
        Assert.Equal(0.667, summary.RetrievalHitRate);   // 2 of 3 answerable retrieved
        Assert.Equal(0.5, summary.MeanReciprocalRank);   // (1 + 1/2 + 0) / 3
        Assert.Equal(0.667, summary.AnswerRate);         // 2 of 3 answerable answered
        Assert.Equal(0.5, summary.CitationAccuracy);     // 1 of 2 answered cited the right doc
        Assert.Equal(0.75, summary.KeyFactCoverage);
        Assert.Equal(0.75, summary.AbstentionAccuracy);  // 3 of 4 correct
        Assert.Equal(0.75, summary.Groundedness);
    }

    [Theory]
    [InlineData("""{"score": 5, "unsupported_claims": []}""", 1.0, null)]
    [InlineData("""Sure! {"score": 3, "unsupported_claims": ["costs $5"]} hope that helps""", 0.5, "Unsupported: costs $5")]
    [InlineData("""{"score": 9}""", null, "no valid score")]
    [InlineData("""I think it's fine.""", null, "no JSON")]
    [InlineData("""{"score": oops}""", null, "malformed")]
    public void Judge_verdicts_are_parsed_defensively(string text, double? expectedScore, string? notesFragment)
    {
        var verdict = GroundednessJudge.Parse(text);

        Assert.Equal(expectedScore, verdict.Score);
        if (notesFragment is null)
        {
            Assert.Null(verdict.Notes);
        }
        else
        {
            Assert.Contains(notesFragment, verdict.Notes, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Percentile_uses_nearest_rank()
    {
        var values = Enumerable.Range(1, 100).Select(i => (double)i).ToList();

        Assert.Equal(50, AiMetricsService.Percentile(values, 0.50));
        Assert.Equal(95, AiMetricsService.Percentile(values, 0.95));
        Assert.Null(AiMetricsService.Percentile([], 0.5));
    }

    [Fact]
    public void Fake_judge_penalizes_claims_absent_from_sources()
    {
        const string grounded = "<sources>\n[1] Invoices are issued on the first day.\n</sources>\n\n<answer>\nInvoices are issued on the first day [1]\n</answer>";
        const string invented = "<sources>\n[1] Invoices are issued on the first day.\n</sources>\n\n<answer>\nInvoices cost nothing during leap years [1]\n</answer>";

        Assert.Equal(1.0, GroundednessJudge.Parse(FakeChatClient.Judge(grounded)).Score);
        Assert.True(GroundednessJudge.Parse(FakeChatClient.Judge(invented)).Score < 0.75);
    }
}
