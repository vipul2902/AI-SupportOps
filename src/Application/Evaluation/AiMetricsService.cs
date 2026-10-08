using AISupportOps.Application.Common;
using AISupportOps.Domain.Agents;
using AISupportOps.Domain.Chat;
using Microsoft.EntityFrameworkCore;

namespace AISupportOps.Application.Evaluation;

/// <summary>
/// Online metrics computed from what production already records on every assistant message and
/// tool call. Aggregation happens in memory over at most <see cref="MaxRows"/> recent answers:
/// simple and exact at this scale. At high volume this would move to pre-aggregated tables or the
/// observability backend (Phase 12).
/// </summary>
public sealed class AiMetricsService(IApplicationDbContext db, TimeProvider time)
{
    public const int MaxRows = 20_000;

    public async Task<AiMetrics> GetAsync(int days, CancellationToken ct)
    {
        days = Math.Clamp(days, 1, 365);
        var since = time.GetUtcNow().AddDays(-days);

        var rows = await db.Messages.AsNoTracking()
            .Where(m => m.Role == MessageRole.Assistant && m.CreatedAt >= since)
            .OrderByDescending(m => m.CreatedAt)
            .Take(MaxRows)
            .Select(m => new { m.CreatedAt, m.Outcome, m.Status, m.LatencyMs, m.InputTokens, m.OutputTokens, m.FeedbackHelpful, m.Citations })
            .ToListAsync(ct);

        // RAG answers only for answer-quality rates (agent runs are measured by tool stats).
        var rag = rows.Where(r => r.Outcome is not null && !r.Outcome.StartsWith("Agent", StringComparison.Ordinal) && r.Status != MessageStatus.Failed).ToList();
        var answered = rag.Where(r => r.Outcome is "Answered" or "Uncited").ToList();
        var latencies = rows.Where(r => r.LatencyMs is not null).Select(r => (double)r.LatencyMs!.Value).Order().ToList();
        var helpful = rows.Count(r => r.FeedbackHelpful == true);
        var unhelpful = rows.Count(r => r.FeedbackHelpful == false);

        var daily = rows
            .GroupBy(r => DateOnly.FromDateTime(r.CreatedAt.UtcDateTime))
            .OrderBy(g => g.Key)
            .Select(g => new DailyPoint(g.Key, g.Count(), g.Count(r => r.Outcome == "Answered"),
                g.Any(r => r.LatencyMs is not null) ? Math.Round(g.Where(r => r.LatencyMs is not null).Average(r => r.LatencyMs!.Value), 1) : null))
            .ToList();

        var tools = (await db.ToolExecutions.AsNoTracking()
                .Where(t => t.CreatedAt >= since)
                .GroupBy(t => t.ToolName)
                .Select(g => new
                {
                    Tool = g.Key,
                    Calls = g.Count(),
                    Succeeded = g.Count(t => t.Status == ToolExecutionStatus.Succeeded),
                    Denied = g.Count(t => t.Status == ToolExecutionStatus.Denied),
                    Invalid = g.Count(t => t.Status == ToolExecutionStatus.InvalidArguments),
                    Failed = g.Count(t => t.Status == ToolExecutionStatus.Failed),
                    AverageLatencyMs = g.Average(t => (double)t.LatencyMs),
                })
                .ToListAsync(ct))
            .Select(t => new ToolStat(t.Tool, t.Calls, t.Succeeded, t.Denied, t.Invalid, t.Failed, Math.Round(t.AverageLatencyMs, 1)))
            .OrderByDescending(t => t.Calls)
            .ToList();

        var latestRun = await db.EvaluationRuns.AsNoTracking()
            .Where(r => r.CompletedAt != null)
            .OrderByDescending(r => r.CompletedAt)
            .FirstOrDefaultAsync(ct);

        return new AiMetrics(
            since,
            rows.Count,
            rows.GroupBy(r => r.Outcome ?? "Unknown").ToDictionary(g => g.Key, g => g.Count()),
            Rate(answered.Count, rag.Count),
            Rate(rag.Count(r => r.Outcome is "NoRelevantSources" or "Declined"), rag.Count),
            Rate(answered.Count(r => r.Outcome == "Uncited"), answered.Count),
            answered.Count == 0 ? null : Math.Round(answered.Average(r => r.Citations.Count), 2),
            answered.Any(r => r.Citations.Count > 0) ? Math.Round(answered.Where(r => r.Citations.Count > 0).Average(r => r.Citations.Max(c => c.Score)), 3) : null,
            new LatencyStats(
                latencies.Count == 0 ? null : Math.Round(latencies.Average(), 1),
                Percentile(latencies, 0.50),
                Percentile(latencies, 0.95)),
            rows.Sum(r => r.InputTokens ?? 0),
            rows.Sum(r => r.OutputTokens ?? 0),
            helpful,
            unhelpful,
            Rate(helpful, helpful + unhelpful),
            daily,
            tools,
            latestRun is null ? null : EvaluationService.ToSummary(latestRun));
    }

    private static double? Rate(int part, int whole) => whole == 0 ? null : Math.Round((double)part / whole, 3);

    /// <summary>Nearest-rank percentile over sorted values.</summary>
    public static double? Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0)
        {
            return null;
        }

        var rank = (int)Math.Ceiling(p * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }
}
