using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AISupportOps.Application.Common;

/// <summary>
/// The application's OpenTelemetry instruments. Uses only BCL types (ActivitySource, Meter), so the
/// Application layer stays free of exporter/vendor packages; the API decides where data goes.
///
/// Rules: metric tags are low-cardinality (outcome, tool, status, kind), never user ids, tenant ids,
/// or text. Span attributes may carry ids for drill-down but never prompts, answers, or documents.
/// </summary>
public static class Telemetry
{
    public const string Name = "AISupportOps";

    /// <summary>Source name for Microsoft.Extensions.AI's GenAI spans and metrics (LLM and embedding calls).</summary>
    public const string AiName = "AISupportOps.AI";

    public static readonly ActivitySource Source = new(Name);

    public static readonly Meter Meter = new(Name);

    // ---- RAG / chat ----
    public static readonly Histogram<double> RetrievalDuration =
        Meter.CreateHistogram<double>("aisupportops.rag.retrieval.duration", "ms", "Query embedding + vector search time.");

    public static readonly Histogram<double> GenerationDuration =
        Meter.CreateHistogram<double>("aisupportops.rag.generation.duration", "ms", "LLM answer generation time.");

    public static readonly Counter<long> Answers =
        Meter.CreateCounter<long>("aisupportops.rag.answers", "{answer}", "Answers by outcome (Answered, Declined, Uncited, NoRelevantSources) and channel.");

    public static readonly Counter<long> Tokens =
        Meter.CreateCounter<long>("aisupportops.ai.tokens", "{token}", "LLM tokens by direction (input/output) and operation.");

    // ---- agent ----
    public static readonly Counter<long> ToolCalls =
        Meter.CreateCounter<long>("aisupportops.agent.tool.calls", "{call}", "Agent tool calls by tool and status.");

    public static readonly Histogram<double> ToolDuration =
        Meter.CreateHistogram<double>("aisupportops.agent.tool.duration", "ms", "Agent tool execution time.");

    // ---- ingestion ----
    public static readonly Counter<long> DocumentsProcessed =
        Meter.CreateCounter<long>("aisupportops.ingestion.documents", "{document}", "Documents processed by result (processed/failed/retry).");

    public static readonly Histogram<double> IngestionDuration =
        Meter.CreateHistogram<double>("aisupportops.ingestion.duration", "ms", "Extract + chunk + embed time per document.");

    public static readonly Counter<long> ChunksCreated =
        Meter.CreateCounter<long>("aisupportops.ingestion.chunks", "{chunk}", "Chunks created.");

    // ---- infrastructure behaviour ----
    public static readonly Counter<long> EmbeddingCacheLookups =
        Meter.CreateCounter<long>("aisupportops.cache.embedding.lookups", "{lookup}", "Query-embedding cache lookups by result (hit/miss/error).");

    public static readonly Counter<long> RateLimitRejections =
        Meter.CreateCounter<long>("aisupportops.ratelimit.rejections", "{request}", "Requests rejected by rate limiting, by policy.");

    public static void RecordTokens(string operation, long? input, long? output)
    {
        if (input is > 0)
        {
            Tokens.Add(input.Value, new("direction", "input"), new("operation", operation));
        }

        if (output is > 0)
        {
            Tokens.Add(output.Value, new("direction", "output"), new("operation", operation));
        }
    }

    /// <summary>Marks a span as failed in the OpenTelemetry-standard way.</summary>
    public static void SetError(this Activity? activity, string description) =>
        activity?.SetStatus(ActivityStatusCode.Error, description);
}
