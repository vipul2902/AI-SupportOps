using System.Diagnostics;
using AISupportOps.Application.Common;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AISupportOps.Api.Infrastructure;

/// <summary>
/// OpenTelemetry wiring: traces, metrics, and logs share one resource (service name/version/env) and
/// are exported over OTLP when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set: the Aspire Dashboard
/// locally, any OTLP backend or Azure Monitor in production. Without an endpoint, instrumentation is
/// still active (cheap) but nothing is exported.
/// </summary>
internal static class Observability
{
    public static WebApplicationBuilder AddObservability(this WebApplicationBuilder builder)
    {
        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        var serviceVersion = typeof(Observability).Assembly.GetName().Version?.ToString() ?? "0.0.0";

        // Machine-readable logs in containers; human-readable locally. Scopes carry TraceId/SpanId.
        if (!builder.Environment.IsDevelopment())
        {
            builder.Logging.AddJsonConsole(o =>
            {
                o.IncludeScopes = true;
                o.UseUtcTimestamp = true;
            });
        }

        builder.Logging.AddOpenTelemetry(o =>
        {
            o.IncludeFormattedMessage = true;
            o.IncludeScopes = true;
        });

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r
                .AddService("aisupportops-api", serviceVersion: serviceVersion)
                .AddAttributes([new("deployment.environment.name", builder.Environment.EnvironmentName)]))
            .WithTracing(t => t
                .AddSource(Telemetry.Name, Telemetry.AiName)
                .AddAspNetCoreInstrumentation(o =>
                {
                    // Health probes run every few seconds; tracing them is pure noise and cost.
                    o.Filter = http => !http.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase);
                    o.RecordException = true;
                })
                .AddHttpClientInstrumentation()
                .AddNpgsql())
            .WithMetrics(m => m
                .AddMeter(Telemetry.Name, Telemetry.AiName, "Npgsql")
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                // Latency histograms in milliseconds need ms-scale buckets (the defaults are seconds-scale).
                .AddView(instrument => instrument.Unit == "ms"
                    ? new ExplicitBucketHistogramConfiguration { Boundaries = [5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000, 30000] }
                    : null));

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            otel.UseOtlpExporter();
        }

        return builder;
    }

    /// <summary>The W3C trace id (when tracing is active), so a support ticket quoting it leads straight to the trace.</summary>
    public static string CurrentTraceId(HttpContext http) =>
        Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier;
}
