using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AISupportOps.Application.Documents;
using AISupportOps.Application.Evaluation;
using AISupportOps.Domain.Documents;
using AISupportOps.IntegrationTests.Fixtures;
using Xunit.Abstractions;
using static AISupportOps.IntegrationTests.Fixtures.TestApi;

namespace AISupportOps.EvaluationTests;

[CollectionDefinition(Name)]
public sealed class EvaluationCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "evaluation";
}

/// <summary>
/// Quality regression gate: runs the versioned dataset (evaluation/dataset.json) over its documents
/// (evaluation/docs) through the real pipeline and fails if quality drops below the thresholds.
/// Uses the offline Fake providers, so it is deterministic and free to run in CI. With real models,
/// run the same dataset via POST /api/evaluations/runs and track the numbers over time.
/// </summary>
[Collection(EvaluationCollection.Name)]
public class RagEvaluationTests(ApiFactory factory, ITestOutputHelper output)
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "evaluation");

    [Fact]
    public async Task Sample_dataset_meets_quality_thresholds()
    {
        using var anonymous = factory.CreateClient();
        using var client = factory.CreateClient(await anonymous.RegisterAsync(org: "Evaluation Org"));
        foreach (var file in Directory.GetFiles(Path.Combine(Root, "docs"), "*.md"))
        {
            await UploadAndWaitAsync(client, Path.GetFileName(file), await File.ReadAllBytesAsync(file));
        }

        var dataset = JsonSerializer.Deserialize<DatasetFile>(await File.ReadAllTextAsync(Path.Combine(Root, "dataset.json")), Json)!;
        var response = await client.PostAsJsonAsync("/api/evaluations/runs", new RunEvaluationRequest(dataset.Name, dataset.Cases, UseLlmJudge: true), Json);
        var run = await response.ReadAsync<EvaluationRunDetail>(HttpStatusCode.OK);

        Report(run);
        var s = run.Run.Summary;
        Assert.Equal(dataset.Cases.Count, s.CaseCount);
        Assert.True(s.RetrievalHitRate >= 0.9, $"Retrieval hit rate {s.RetrievalHitRate} < 0.9");
        Assert.True(s.MeanReciprocalRank >= 0.8, $"MRR {s.MeanReciprocalRank} < 0.8");
        Assert.True(s.CitationAccuracy >= 0.9, $"Citation accuracy {s.CitationAccuracy} < 0.9");
        Assert.True(s.KeyFactCoverage >= 0.7, $"Key fact coverage {s.KeyFactCoverage} < 0.7");
        // Strict where it matters most: every unanswerable question must abstain (no hallucinated answers).
        Assert.All(run.Results.Where(r => r.ShouldAbstain), r => Assert.True(r.AbstentionCorrect, $"Hallucinated an answer for '{r.CaseId}': {r.Answer}"));
        // Realistic elsewhere: the offline embedder matches words, not meaning, so a paraphrase can be missed.
        Assert.True(s.AbstentionAccuracy >= 0.9, $"Abstention accuracy {s.AbstentionAccuracy} < 0.9");
        Assert.True(s.Groundedness >= 0.8, $"Groundedness {s.Groundedness} < 0.8");

        // The run is persisted and shows up in the metrics overview.
        var metrics = await (await client.GetAsync(new Uri("/api/evaluations/metrics", UriKind.Relative))).ReadAsync<AiMetrics>(HttpStatusCode.OK);
        Assert.Equal(run.Run.Id, metrics.LatestEvaluation?.Id);
    }

    private void Report(EvaluationRunDetail run)
    {
        var s = run.Run.Summary;
        output.WriteLine($"Run '{run.Run.Name}' prompt={run.Run.PromptId} chat={run.Run.ChatModel} embed={run.Run.EmbeddingModel}");
        output.WriteLine($"hit@k={s.RetrievalHitRate} mrr={s.MeanReciprocalRank} answerRate={s.AnswerRate} citationAcc={s.CitationAccuracy} " +
                         $"keyFacts={s.KeyFactCoverage} abstention={s.AbstentionAccuracy} groundedness={s.Groundedness} avgLatency={s.AverageLatencyMs}ms");
        foreach (var r in run.Results)
        {
            output.WriteLine($"  {(r.AbstentionCorrect ? "ok " : "BAD")} {r.CaseId,-20} {r.Outcome,-18} rank={r.ExpectedSourceRank?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"} " +
                             $"cited={r.CitedExpectedSource?.ToString() ?? "-"} facts={r.KeyFactCoverage?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"} | {r.Answer}");
        }
    }

    private sealed record DatasetFile(string Name, IReadOnlyList<EvaluationCase> Cases);

    private static async Task UploadAndWaitAsync(HttpClient client, string fileName, byte[] content)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/markdown");
        form.Add(file, "file", fileName);
        var created = await (await client.PostAsync(new Uri("/api/documents", UriKind.Relative), form)).ReadAsync<DocumentResponse>(HttpStatusCode.Created);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var doc = await (await client.GetAsync(new Uri($"/api/documents/{created.Id}", UriKind.Relative))).ReadAsync<DocumentResponse>(HttpStatusCode.OK);
            if (doc.Status is DocumentStatus.Processed or DocumentStatus.Failed || DateTime.UtcNow > deadline)
            {
                Assert.Equal(DocumentStatus.Processed, doc.Status);
                return;
            }

            await Task.Delay(100);
        }
    }
}
