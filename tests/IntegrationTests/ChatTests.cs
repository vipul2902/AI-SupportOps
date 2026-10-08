using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AISupportOps.Application.Ai;
using AISupportOps.Application.Chat;
using AISupportOps.Application.Documents;
using AISupportOps.Application.Identity;
using AISupportOps.Application.Knowledge;
using AISupportOps.Domain.Chat;
using AISupportOps.Domain.Documents;
using AISupportOps.Domain.Tenants;
using AISupportOps.Infrastructure.Ai;
using AISupportOps.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using static AISupportOps.IntegrationTests.Fixtures.TestApi;

namespace AISupportOps.IntegrationTests;

[Collection(ApiCollection.Name)]
public class ChatTests(ApiFactory factory)
{
    private const string PasswordDoc = "# Account security\n\nTo reset your password, open Settings, choose Security, then click Reset password. A reset link is emailed to you.";

    [Fact]
    public async Task New_conversation_streams_meta_deltas_and_done_and_persists_both_messages()
    {
        using var client = factory.CreateClient(await NewTenantAsync());
        await UploadAndWaitAsync(client, "security.md", PasswordDoc);

        var events = await ChatAsync(client, new ChatRequest("How do I reset my password?"));

        Assert.Equal("meta", events[0].Type);
        Assert.Equal("done", events[^1].Type);
        Assert.True(events.Count(e => e.Type == "delta") > 1, "answer should arrive in several deltas");
        var meta = events[0].As<ChatMetaEvent>();
        // The event type travels in the SSE "event:" field only, never duplicated in the JSON payload.
        Assert.DoesNotContain(events, e => e.Data.Contains("eventType", StringComparison.OrdinalIgnoreCase));
        var done = events[^1].As<ChatDoneEvent>();
        var streamed = string.Concat(events.Where(e => e.Type == "delta").Select(e => e.As<ChatDeltaEvent>().Text));

        Assert.Equal(AnswerOutcome.Answered, done.Outcome);
        Assert.Equal("security.md", Assert.Single(done.Citations).FileName);
        Assert.Equal("How do I reset my password?", meta.ConversationTitle);

        var detail = await GetConversationAsync(client, meta.ConversationId);
        Assert.Collection(detail.Messages,
            m => Assert.Equal((MessageRole.User, "How do I reset my password?"), (m.Role, m.Content)),
            m =>
            {
                Assert.Equal(MessageRole.Assistant, m.Role);
                Assert.Equal(streamed, m.Content);
                Assert.Equal(MessageStatus.Completed, m.Status);
                Assert.Equal("security.md", Assert.Single(m.Citations).FileName); // jsonb round-trip
            });
    }

    [Fact]
    public async Task Follow_up_is_rewritten_with_conversation_context_before_retrieval()
    {
        using var client = factory.CreateClient(await NewTenantAsync());
        await UploadAndWaitAsync(client, "security.md", PasswordDoc);
        var first = await ChatAsync(client, new ChatRequest("How do I reset my password?"));
        var conversationId = first[0].As<ChatMetaEvent>().ConversationId;

        var second = await ChatAsync(client, new ChatRequest("Where is the link sent?", conversationId));

        var done = second[^1].As<ChatDoneEvent>();
        Assert.Contains("reset my password", done.RetrievalQuery, StringComparison.Ordinal);
        Assert.Equal(AnswerOutcome.Answered, done.Outcome);
        Assert.Equal(4, (await GetConversationAsync(client, conversationId)).Messages.Count);
    }

    [Fact]
    public async Task Unanswerable_question_streams_honest_no_answer()
    {
        using var client = factory.CreateClient(await NewTenantAsync());

        var events = await ChatAsync(client, new ChatRequest("What is the enterprise refund policy?"));

        Assert.Equal(RagService.NoAnswerMessage, events.Single(e => e.Type == "delta").As<ChatDeltaEvent>().Text);
        Assert.Equal(AnswerOutcome.NoRelevantSources, events[^1].As<ChatDoneEvent>().Outcome);
    }

    [Fact]
    public async Task Conversations_are_private_to_their_creator_even_within_a_tenant()
    {
        var owner = await NewTenantAsync();
        var colleague = await factory.AddMemberAsync(owner, TenantRole.Admin);
        using var ownerClient = factory.CreateClient(owner);
        using var colleagueClient = factory.CreateClient(colleague);
        var conversationId = (await ChatAsync(ownerClient, new ChatRequest("private question")))[0].As<ChatMetaEvent>().ConversationId;

        var read = await colleagueClient.GetAsync(new Uri($"/api/conversations/{conversationId}", UriKind.Relative));
        var post = await colleagueClient.PostAsJsonAsync("/api/chat", new ChatRequest("hijack", conversationId), Json);
        var list = await (await colleagueClient.GetAsync(new Uri("/api/conversations", UriKind.Relative)))
            .ReadAsync<PagedResponse<ConversationSummary>>(HttpStatusCode.OK);

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode); // a real status: checked before streaming starts
        Assert.Equal(0, list.TotalCount);
    }

    [Fact]
    public async Task Llm_failure_mid_stream_emits_error_event_and_persists_failed_message()
    {
        using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IAiChatService>(new BreaksMidStream())));
        using var client = app.CreateClient(await NewTenantAsync(app));
        await UploadAndWaitAsync(client, "security.md", PasswordDoc);

        var events = await ChatAsync(client, new ChatRequest("How do I reset my password?"));

        Assert.Equal("partial ", events.Single(e => e.Type == "delta").As<ChatDeltaEvent>().Text);
        var error = events[^1].As<ChatErrorEvent>();
        Assert.Equal("error", events[^1].Type);
        Assert.Contains("unavailable", error.Message, StringComparison.Ordinal);

        var detail = await GetConversationAsync(client, events[0].As<ChatMetaEvent>().ConversationId);
        var assistant = detail.Messages[^1];
        Assert.Equal((MessageStatus.Failed, "partial "), (assistant.Status, assistant.Content));
    }

    [Fact]
    public async Task Invalid_requests_fail_with_real_status_codes_before_streaming()
    {
        using var anonymous = factory.CreateClient();
        using var client = factory.CreateClient(await NewTenantAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/chat", new ChatRequest("hi"), Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/chat", new ChatRequest(""), Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/chat", new ChatRequest("hi", Guid.NewGuid()), Json)).StatusCode);
    }

    [Fact]
    public async Task Conversations_can_be_listed_renamed_and_deleted()
    {
        using var client = factory.CreateClient(await NewTenantAsync());
        var older = (await ChatAsync(client, new ChatRequest("first topic")))[0].As<ChatMetaEvent>().ConversationId;
        var newer = (await ChatAsync(client, new ChatRequest("second topic")))[0].As<ChatMetaEvent>().ConversationId;

        var list = await (await client.GetAsync(new Uri("/api/conversations", UriKind.Relative))).ReadAsync<PagedResponse<ConversationSummary>>(HttpStatusCode.OK);
        var renamed = await (await client.PatchAsJsonAsync($"/api/conversations/{older}", new RenameConversationRequest("Renamed"), Json))
            .ReadAsync<ConversationSummary>(HttpStatusCode.OK);
        var delete = await client.DeleteAsync(new Uri($"/api/conversations/{newer}", UriKind.Relative));

        Assert.Equal([newer, older], list.Items.Select(c => c.Id));
        Assert.Equal("Renamed", renamed.Title);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(new Uri($"/api/conversations/{newer}", UriKind.Relative))).StatusCode);
    }

    // ---- SSE client ----

    private sealed record SseEvent(string Type, string Data)
    {
        public T As<T>() => JsonSerializer.Deserialize<T>(Data, Json)!;
    }

    private static async Task<List<SseEvent>> ChatAsync(HttpClient client, ChatRequest request)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/chat") { Content = JsonContent.Create(request, options: Json) };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var events = new List<SseEvent>();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        string? type = null;
        var data = new StringBuilder();
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length == 0)
            {
                if (type is not null)
                {
                    events.Add(new SseEvent(type, data.ToString()));
                }

                type = null;
                data.Clear();
            }
            else if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                type = line["event: ".Length..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                data.Append(line["data: ".Length..]);
            }
        }

        return events;
    }

    // ---- doubles & helpers ----

    /// <summary>Streams one delta, then the provider "fails".</summary>
    private sealed class BreaksMidStream : IAiChatService
    {
        public string ModelId => "breaks";

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct) =>
            Task.FromResult(new LlmResponse(FakeChatClient.Rewrite(request.Messages[^1].Content), ModelId, new LlmUsage(1, 1), "stop"));

        public async IAsyncEnumerable<LlmStreamUpdate> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new LlmStreamUpdate("partial ");
            await Task.Yield();
            throw new AiUnavailableException("The AI model is currently unavailable.");
        }
    }

    private async Task<AuthResponse> NewTenantAsync(WebApplicationFactory<Program>? app = null)
    {
        using var client = (app ?? factory).CreateClient();
        return await client.RegisterAsync();
    }

    private static async Task<ConversationDetail> GetConversationAsync(HttpClient client, Guid id) =>
        await (await client.GetAsync(new Uri($"/api/conversations/{id}", UriKind.Relative))).ReadAsync<ConversationDetail>(HttpStatusCode.OK);

    private static async Task UploadAndWaitAsync(HttpClient client, string fileName, string content)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
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
