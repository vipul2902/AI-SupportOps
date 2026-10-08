using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using AISupportOps.Application.Ai;
using AISupportOps.Application.Chat;
using AISupportOps.Application.Identity;
using AISupportOps.Application.Knowledge;
using AISupportOps.Domain.Documents;
using AISupportOps.Infrastructure.Ai;
using AISupportOps.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using static AISupportOps.IntegrationTests.Fixtures.TestApi;

namespace AISupportOps.IntegrationTests;

/// <summary>Redis-backed features against a real Redis container.</summary>
[Collection(ApiCollection.Name)]
public class RedisFeatureTests(ApiFactory factory)
{
    [Fact]
    public async Task Repeated_query_embeddings_are_served_from_redis()
    {
        var generator = new CountingGenerator();
        using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(generator)));
        using var client = app.CreateClient(await RegisterAsync(app));
        var query = $"cache probe {Guid.NewGuid():N}";

        await SearchAsync(client, query);
        await SearchAsync(client, query);
        await SearchAsync(client, query);

        Assert.Equal(1, generator.CallsFor(query));
    }

    [Fact]
    public async Task Ai_rate_limit_is_shared_across_api_instances()
    {
        // Two independent API hosts (as if behind a load balancer) sharing one Redis.
        using var instanceA = WithAiLimit(3);
        using var instanceB = WithAiLimit(3);
        var user = await RegisterAsync(instanceA);
        using var a = instanceA.CreateClient(user);
        using var b = instanceB.CreateClient(user);

        var statuses = new List<HttpStatusCode>();
        foreach (var client in new[] { a, b, a, b })
        {
            statuses.Add((await client.PostAsJsonAsync("/api/ask", new AskRequest("anything"), Json)).StatusCode);
        }

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);

        var limited = await b.PostAsJsonAsync("/api/ask", new AskRequest("anything"), Json);
        Assert.True(int.Parse(limited.Headers.RetryAfter!.ToString(), System.Globalization.CultureInfo.InvariantCulture) is > 0 and <= 60);
        Assert.Contains("Rate limit exceeded", await limited.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // A different user has their own budget.
        using var other = instanceA.CreateClient(await RegisterAsync(instanceA));
        Assert.Equal(HttpStatusCode.OK, (await other.PostAsJsonAsync("/api/ask", new AskRequest("anything"), Json)).StatusCode);
    }

    [Fact]
    public async Task Old_turns_are_folded_into_a_summary_that_reaches_later_prompts()
    {
        var recorder = new RecordingChat();
        using var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Conversations:HistoryMaxMessages", "2");
            b.UseSetting("Conversations:SummarizeBatchSize", "2");
            b.ConfigureTestServices(s => s.AddSingleton<IAiChatService>(recorder));
        });
        using var client = app.CreateClient(await RegisterAsync(app));
        await UploadAsync(client, "plans.md", "# Plans\n\nThe Enterprise plan includes SSO and a 99.9% uptime SLA.");

        var conversationId = await ChatAsync(client, "I am on the Enterprise plan. Does it include SSO?", null);
        await ChatAsync(client, "What uptime does it promise?", conversationId);   // 4 messages → 2 overflow → summarized
        await ChatAsync(client, "Thanks, anything else in my plan?", conversationId);

        var detail = await (await client.GetAsync(new Uri($"/api/conversations/{conversationId}", UriKind.Relative)))
            .ReadAsync<ConversationDetail>(HttpStatusCode.OK);
        Assert.NotNull(detail.Summary);
        Assert.Contains("Enterprise plan", detail.Summary, StringComparison.Ordinal);
        Assert.Equal(6, detail.Messages.Count); // full history is still persisted

        // The latest answer prompt carried the summary as long-range memory.
        var lastAnswer = recorder.Requests.Last(r => r.SystemPrompt.Contains("support knowledge assistant", StringComparison.Ordinal));
        Assert.Contains(lastAnswer.Messages, m => m.Content.Contains("<conversation_summary>", StringComparison.Ordinal)
                                                   && m.Content.Contains("Enterprise plan", StringComparison.Ordinal));
    }

    // ---- doubles ----

    private sealed class CountingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly FakeEmbeddingGenerator _inner = new();
        private readonly ConcurrentDictionary<string, int> _calls = new();

        public int CallsFor(string text) => _calls.Where(kv => kv.Key.Contains(text, StringComparison.Ordinal)).Sum(kv => kv.Value);

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = values.ToList();
            list.ForEach(v => _calls.AddOrUpdate(v, 1, (_, n) => n + 1));
            return _inner.GenerateAsync(list, options, cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => _inner.Dispose();
    }

    private sealed class RecordingChat : IAiChatService, IDisposable
    {
        private readonly FakeChatClient _fake = new();

        public void Dispose() => _fake.Dispose();

        public ConcurrentQueue<LlmRequest> Requests { get; } = new();

        public string ModelId => FakeChatClient.ModelId;

        public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            Requests.Enqueue(request);
            var response = await _fake.GetResponseAsync(ToMessages(request), cancellationToken: ct);
            return new LlmResponse(response.Text, ModelId, new LlmUsage(1, 1), "stop");
        }

        public async IAsyncEnumerable<LlmStreamUpdate> StreamAsync(LlmRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var response = await CompleteAsync(request, ct);
            yield return new LlmStreamUpdate(response.Text, response.Usage, ModelId);
        }

        private static List<ChatMessage> ToMessages(LlmRequest r) =>
            [new(ChatRole.System, r.SystemPrompt), .. r.Messages.Select(m => new ChatMessage(m.Role == LlmRole.User ? ChatRole.User : ChatRole.Assistant, m.Content))];
    }

    // ---- helpers ----

    private WebApplicationFactory<Program> WithAiLimit(int perMinute) =>
        factory.WithWebHostBuilder(b => b.UseSetting("RateLimiting:AiRequestsPerMinute", perMinute.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private static async Task<AuthResponse> RegisterAsync(WebApplicationFactory<Program> app)
    {
        using var client = app.CreateClient();
        return await client.RegisterAsync();
    }

    private static async Task SearchAsync(HttpClient client, string query) =>
        await (await client.PostAsJsonAsync("/api/search", new SearchRequest(query), Json)).ReadAsync<SearchResponse>(HttpStatusCode.OK);

    private static async Task<Guid> ChatAsync(HttpClient client, string message, Guid? conversationId)
    {
        using var response = await client.PostAsJsonAsync("/api/chat", new ChatRequest(message, conversationId), Json);
        var body = await response.Content.ReadAsStringAsync(); // reads to the end: the turn (and summarization) is complete
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("event: done", body, StringComparison.Ordinal);
        var metaData = body.Split('\n').First(l => l.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..];
        return System.Text.Json.JsonSerializer.Deserialize<ChatMetaEvent>(metaData, Json)!.ConversationId;
    }

    private static async Task UploadAsync(HttpClient client, string fileName, string content)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(content));
        form.Add(file, "file", fileName);
        var created = await (await client.PostAsync(new Uri("/api/documents", UriKind.Relative), form))
            .ReadAsync<Application.Documents.DocumentResponse>(HttpStatusCode.Created);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while ((await (await client.GetAsync(new Uri($"/api/documents/{created.Id}", UriKind.Relative)))
                   .ReadAsync<Application.Documents.DocumentResponse>(HttpStatusCode.OK)).Status != DocumentStatus.Processed
               && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }
    }
}
