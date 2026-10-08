using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AISupportOps.Application.Common;
using AISupportOps.Application.Documents;
using AISupportOps.Application.Knowledge;
using AISupportOps.Domain.Documents;
using AISupportOps.IntegrationTests.Fixtures;
using static AISupportOps.IntegrationTests.Fixtures.TestApi;

namespace AISupportOps.IntegrationTests;

/// <summary>
/// Verifies the telemetry contract: the right spans in the right hierarchy, the right metrics with
/// low-cardinality tags, and no customer text leaking into telemetry. Uses in-process listeners,
/// independent of any exporter.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ObservabilityTests(ApiFactory factory) : IDisposable
{
    private readonly ConcurrentQueue<Activity> _spans = new();
    private readonly ConcurrentQueue<(string Name, double Value, Dictionary<string, object?> Tags)> _measurements = new();
    private ActivityListener? _activityListener;
    private MeterListener? _meterListener;

    [Fact]
    public async Task Rag_request_produces_a_connected_trace_down_to_sql_and_llm_calls()
    {
        using var client = factory.CreateClient(await NewTenantAsync());
        await UploadAndWaitAsync(client, "security.md", "# Account security\n\nTo reset your password, open Settings, choose Security, then click Reset password.");
        StartListening();

        await (await client.PostAsJsonAsync("/api/ask", new AskRequest("How do I reset my password?"), Json)).ReadAsync<AskResponse>(HttpStatusCode.OK);

        var ask = Assert.Single(_spans, s => s.OperationName == "rag.ask");
        var prepare = Assert.Single(_spans, s => s.OperationName == "rag.prepare" && s.TraceId == ask.TraceId);
        var search = Assert.Single(_spans, s => s.OperationName == "vector.search" && s.TraceId == ask.TraceId);
        Assert.Equal(ask.SpanId, prepare.ParentSpanId);
        Assert.Equal(prepare.SpanId, search.ParentSpanId);
        Assert.Equal("Answered", ask.GetTagItem("rag.outcome"));
        Assert.True((int)prepare.GetTagItem("rag.retrieved")! >= 1);

        // Library spans join the same trace: SQL under the vector search, GenAI spans for model calls.
        Assert.Contains(_spans, s => s.Source.Name == "Npgsql" && s.TraceId == ask.TraceId);
        Assert.Contains(_spans, s => s.Source.Name == Telemetry.AiName && s.TraceId == ask.TraceId && s.OperationName.StartsWith("chat", StringComparison.Ordinal));
        Assert.Contains(_spans, s => s.Source.Name == Telemetry.AiName && s.TraceId == ask.TraceId && s.OperationName.StartsWith("embeddings", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ai_metrics_are_recorded_with_low_cardinality_tags()
    {
        using var client = factory.CreateClient(await NewTenantAsync());
        await UploadAndWaitAsync(client, "billing.md", "# Billing\n\nInvoices are issued on the first day of each month.");
        StartListening();

        await (await client.PostAsJsonAsync("/api/ask", new AskRequest("When are invoices issued each month?"), Json)).ReadAsync<AskResponse>(HttpStatusCode.OK);
        await (await client.PostAsJsonAsync("/api/ask", new AskRequest("Is there a student discount?"), Json)).ReadAsync<AskResponse>(HttpStatusCode.OK);
        _meterListener!.RecordObservableInstruments();

        Assert.Contains(_measurements, m => m.Name == "aisupportops.rag.retrieval.duration" && m.Value >= 0);
        Assert.Contains(_measurements, m => m.Name == "aisupportops.rag.generation.duration" && Equals(m.Tags["channel"], "ask"));
        Assert.Contains(_measurements, m => m.Name == "aisupportops.rag.answers" && Equals(m.Tags["outcome"], "Answered"));
        Assert.Contains(_measurements, m => m.Name == "aisupportops.rag.answers" && Equals(m.Tags["outcome"], "NoRelevantSources"));
        Assert.Contains(_measurements, m => m.Name == "aisupportops.ai.tokens" && Equals(m.Tags["direction"], "input") && m.Value > 0);

        // Cardinality guard: our metrics never carry ids or free text as tags.
        var allowedTags = new HashSet<string> { "outcome", "channel", "direction", "operation", "tool", "status", "result", "kind", "policy" };
        Assert.All(_measurements.Where(m => m.Name.StartsWith("aisupportops.", StringComparison.Ordinal)),
            m => Assert.All(m.Tags.Keys, k => Assert.Contains(k, allowedTags)));
    }

    [Fact]
    public async Task Telemetry_never_contains_the_users_question()
    {
        using var client = factory.CreateClient(await NewTenantAsync());
        var secret = $"my-card-number-{Guid.NewGuid():N}";
        StartListening();

        await client.PostAsJsonAsync("/api/ask", new AskRequest($"Why was {secret} declined?"), Json);

        Assert.NotEmpty(_spans);
        Assert.DoesNotContain(_spans, s => s.TagObjects.Any(t => t.Value?.ToString()?.Contains(secret, StringComparison.Ordinal) == true));
    }

    [Fact]
    public async Task Error_responses_carry_the_w3c_trace_id()
    {
        using var client = factory.CreateClient(await NewTenantAsync());
        StartListening();

        var response = await client.GetAsync(new Uri($"/api/tickets/{Guid.NewGuid()}", UriKind.Relative));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var traceId = body.RootElement.GetProperty("traceId").GetString();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Matches("^[0-9a-f]{32}$", traceId);
        Assert.Contains(_spans, s => s.TraceId.ToString() == traceId);
    }

    private void StartListening()
    {
        _activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is Telemetry.Name or Telemetry.AiName or "Npgsql" or "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _spans.Enqueue,
        };
        ActivitySource.AddActivityListener(_activityListener);

        _meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == Telemetry.Name)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        _meterListener.SetMeasurementEventCallback<double>((i, v, tags, _) => Record(i, v, tags));
        _meterListener.SetMeasurementEventCallback<long>((i, v, tags, _) => Record(i, v, tags));
        _meterListener.Start();
    }

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var tag in tags)
        {
            dict[tag.Key] = tag.Value;
        }

        _measurements.Enqueue((instrument.Name, value, dict));
    }

    public void Dispose()
    {
        _activityListener?.Dispose();
        _meterListener?.Dispose();
    }

    private async Task<Application.Identity.AuthResponse> NewTenantAsync()
    {
        using var client = factory.CreateClient();
        return await client.RegisterAsync();
    }

    private static async Task UploadAndWaitAsync(HttpClient client, string fileName, string content)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/markdown");
        form.Add(file, "file", fileName);
        var created = await (await client.PostAsync(new Uri("/api/documents", UriKind.Relative), form)).ReadAsync<DocumentResponse>(HttpStatusCode.Created);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while ((await (await client.GetAsync(new Uri($"/api/documents/{created.Id}", UriKind.Relative))).ReadAsync<DocumentResponse>(HttpStatusCode.OK)).Status != DocumentStatus.Processed
               && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }
    }
}
