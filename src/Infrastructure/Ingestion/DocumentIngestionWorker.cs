using AISupportOps.Application.Common;
using AISupportOps.Application.Ingestion;
using AISupportOps.Domain.Documents;
using AISupportOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AISupportOps.Infrastructure.Ingestion;

/// <summary>
/// Background worker using PostgreSQL as a job queue. Each iteration claims one document with
/// <c>FOR UPDATE SKIP LOCKED</c>, so any number of API instances can run workers concurrently
/// without double-processing. A claimed document records a lease (its UpdatedAt); if a worker
/// dies mid-processing, another claims the document once the lease expires.
/// </summary>
internal sealed partial class DocumentIngestionWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<IngestionOptions> options,
    TimeProvider time,
    ILogger<DocumentIngestionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger, options.Value.PollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            bool processed;
            try
            {
                processed = await ProcessNextAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // A worker loop must survive unexpected errors (e.g. database restart).
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogLoopError(logger, ex);
                processed = false;
            }

            // Drain the queue without delay; sleep only when idle.
            if (!processed)
            {
                await Task.Delay(options.Value.PollInterval, time, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    /// <returns>True if a document was claimed.</returns>
    internal async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        var claim = await ClaimNextAsync(ct);
        if (claim is null)
        {
            return false;
        }

        if (claim.ShouldProcess)
        {
            // Fresh scope per document: its own DbContext, and a tenant scope matching the document.
            await using var scope = scopeFactory.CreateAsyncScope();
            using var tenant = scope.ServiceProvider.GetRequiredService<ITenantScope>().Enter(claim.TenantId);
            await scope.ServiceProvider.GetRequiredService<DocumentIngestionService>().ProcessAsync(claim.DocumentId, ct);
        }

        return true;
    }

    private sealed record Claim(Guid DocumentId, Guid TenantId, bool ShouldProcess);

    private async Task<Claim?> ClaimNextAsync(CancellationToken ct)
    {
        var settings = options.Value;
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var leaseExpiredBefore = time.GetUtcNow() - settings.Lease;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Raw SQL because row locking has no LINQ equivalent. The worker runs outside any tenant,
        // so it bypasses the tenant filter here; processing then runs inside the document's tenant scope.
        var candidates = await db.Documents
            .FromSqlInterpolated($"""
                SELECT * FROM documents
                WHERE status = {nameof(DocumentStatus.Uploaded)}
                   OR (status = {nameof(DocumentStatus.Processing)} AND updated_at < {leaseExpiredBefore})
                ORDER BY created_at
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """)
            .IgnoreQueryFilters()
            .ToListAsync(ct);

        var document = candidates.SingleOrDefault();
        if (document is null)
        {
            await transaction.CommitAsync(ct);
            return null;
        }

        if (document.ProcessingAttempts >= settings.MaxAttempts)
        {
            // Poison document (e.g. it keeps crashing the worker): stop retrying.
            document.MarkFailed($"Processing abandoned after {document.ProcessingAttempts} attempts.");
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            LogAbandoned(logger, document.Id, document.ProcessingAttempts);
            return new Claim(document.Id, document.TenantId, ShouldProcess: false);
        }

        document.BeginProcessingAttempt(); // also refreshes UpdatedAt = the lease start
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        LogClaimed(logger, document.Id, document.TenantId, document.ProcessingAttempts);
        return new Claim(document.Id, document.TenantId, ShouldProcess: true);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Document ingestion worker started (poll interval {PollInterval})")]
    private static partial void LogStarted(ILogger logger, TimeSpan pollInterval);

    [LoggerMessage(Level = LogLevel.Information, Message = "Claimed document {DocumentId} (tenant {TenantId}), attempt {Attempt}")]
    private static partial void LogClaimed(ILogger logger, Guid documentId, Guid tenantId, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Abandoned document {DocumentId} after {Attempts} attempts")]
    private static partial void LogAbandoned(ILogger logger, Guid documentId, int attempts);

    [LoggerMessage(Level = LogLevel.Error, Message = "Document ingestion worker iteration failed")]
    private static partial void LogLoopError(ILogger logger, Exception exception);
}
