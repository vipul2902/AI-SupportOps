using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AISupportOps.Application.Chat;
using AISupportOps.Application.Evaluation;
using AISupportOps.Application.Identity;
using AISupportOps.Domain.Chat;
using AISupportOps.Domain.Tenants;
using AISupportOps.IntegrationTests.Fixtures;
using static AISupportOps.IntegrationTests.Fixtures.TestApi;

namespace AISupportOps.IntegrationTests;

[Collection(ApiCollection.Name)]
public class MetricsTests(ApiFactory factory)
{
    [Fact]
    public async Task Feedback_and_answers_flow_into_ai_metrics()
    {
        var owner = await NewTenantAsync();
        using var client = factory.CreateClient(owner);
        var (conversationId, assistantId) = await ChatAsync(client, "What is the refund policy?"); // empty KB → abstains

        var feedback = await client.PostAsJsonAsync($"/api/conversations/{conversationId}/messages/{assistantId}/feedback",
            new FeedbackRequest(false, "Didn't answer my question"), Json);
        var metrics = await (await client.GetAsync(new Uri("/api/evaluations/metrics?days=7", UriKind.Relative))).ReadAsync<AiMetrics>(HttpStatusCode.OK);

        Assert.Equal(HttpStatusCode.NoContent, feedback.StatusCode);
        Assert.Equal(1, metrics.TotalAnswers);
        Assert.Equal(1, metrics.Outcomes["NoRelevantSources"]);
        Assert.Equal(1.0, metrics.AbstentionRate);
        Assert.Equal((0, 1, 0.0), (metrics.HelpfulFeedback, metrics.UnhelpfulFeedback, metrics.Satisfaction!.Value));
        Assert.NotNull(metrics.Latency.P95Ms);
        Assert.Single(metrics.Daily);
    }

    [Fact]
    public async Task Feedback_rules_privacy_and_role_checks()
    {
        var owner = await NewTenantAsync();
        using var client = factory.CreateClient(owner);
        var (conversationId, assistantId) = await ChatAsync(client, "hello there");
        var detail = await (await client.GetAsync(new Uri($"/api/conversations/{conversationId}", UriKind.Relative))).ReadAsync<ConversationDetail>(HttpStatusCode.OK);
        var userMessageId = detail.Messages.Single(m => m.Role == MessageRole.User).Id;
        var viewer = await factory.AddMemberAsync(owner, TenantRole.Viewer);
        using var viewerClient = factory.CreateClient(viewer);

        var onUserMessage = await client.PostAsJsonAsync($"/api/conversations/{conversationId}/messages/{userMessageId}/feedback", new FeedbackRequest(true), Json);
        var onOthersConversation = await viewerClient.PostAsJsonAsync($"/api/conversations/{conversationId}/messages/{assistantId}/feedback", new FeedbackRequest(true), Json);
        var viewerMetrics = await viewerClient.GetAsync(new Uri("/api/evaluations/metrics", UriKind.Relative));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, onUserMessage.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, onOthersConversation.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, viewerMetrics.StatusCode);
    }

    [Fact]
    public async Task Evaluation_requests_are_validated()
    {
        using var client = factory.CreateClient(await NewTenantAsync());

        var duplicateIds = await client.PostAsJsonAsync("/api/evaluations/runs", new RunEvaluationRequest("x",
            [new EvaluationCase("a", "q1", ShouldAbstain: true), new EvaluationCase("a", "q2", ShouldAbstain: true)]), Json);
        var missingExpectation = await client.PostAsJsonAsync("/api/evaluations/runs", new RunEvaluationRequest("x",
            [new EvaluationCase("a", "q1")]), Json);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicateIds.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missingExpectation.StatusCode);
    }

    private async Task<AuthResponse> NewTenantAsync()
    {
        using var client = factory.CreateClient();
        return await client.RegisterAsync();
    }

    private static async Task<(Guid ConversationId, Guid AssistantMessageId)> ChatAsync(HttpClient client, string message)
    {
        using var response = await client.PostAsJsonAsync("/api/chat", new ChatRequest(message), Json);
        var body = await response.Content.ReadAsStringAsync();
        var data = body.Split('\n').Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Select(l => l["data: ".Length..]).ToList();
        var meta = JsonSerializer.Deserialize<ChatMetaEvent>(data[0], Json)!;
        var done = JsonSerializer.Deserialize<ChatDoneEvent>(data[^1], Json)!;
        return (meta.ConversationId, done.MessageId);
    }
}
