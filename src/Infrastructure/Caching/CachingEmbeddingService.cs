using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AISupportOps.Application.Common;
using AISupportOps.Application.Knowledge;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace AISupportOps.Infrastructure.Caching;

/// <summary>
/// Decorator: cache-aside for query embeddings in Redis. A hit saves a paid API call and
/// ~100–300 ms per question. Keys include the tenant, model, and a hash of the query text:
/// - tenant: no cross-tenant sharing, so one tenant cannot infer another's questions from cache timing;
/// - model: switching models never returns a vector from the wrong vector space;
/// - hash: the raw query (possibly containing customer details) is never stored as a key.
/// Cache failures fail open: the request proceeds with a fresh embedding.
/// </summary>
internal sealed partial class CachingEmbeddingService(
    IEmbeddingService inner,
    IDistributedCache cache,
    ITenantContext tenantContext,
    ILogger<CachingEmbeddingService> logger) : IEmbeddingService
{
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    public string ModelId => inner.ModelId;

    public Task<EmbeddingBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct) => inner.EmbedAsync(inputs, ct);

    public async Task<float[]> EmbedQueryAsync(string query, CancellationToken ct)
    {
        if (tenantContext.TenantId is not { } tenantId)
        {
            return await inner.EmbedQueryAsync(query, ct);
        }

        var key = Key(tenantId, ModelId, query);
        try
        {
            if (await cache.GetAsync(key, ct) is { Length: > 0 } cached)
            {
                LogHit(logger, tenantId);
                Telemetry.EmbeddingCacheLookups.Add(1, new KeyValuePair<string, object?>("result", "hit"));
                return MemoryMarshal.Cast<byte, float>(cached).ToArray();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCacheError(logger, ex);
            Telemetry.EmbeddingCacheLookups.Add(1, new KeyValuePair<string, object?>("result", "error"));
            return await inner.EmbedQueryAsync(query, ct);
        }

        Telemetry.EmbeddingCacheLookups.Add(1, new KeyValuePair<string, object?>("result", "miss"));
        var vector = await inner.EmbedQueryAsync(query, ct);
        try
        {
            await cache.SetAsync(key, MemoryMarshal.AsBytes(vector.AsSpan()).ToArray(),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCacheError(logger, ex);
        }

        return vector;
    }

    public static string Key(Guid tenantId, string model, string query) =>
        $"emb:{tenantId:N}:{model}:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(query)))}";

    [LoggerMessage(Level = LogLevel.Debug, Message = "Query embedding cache hit for tenant {TenantId}")]
    private static partial void LogHit(ILogger logger, Guid tenantId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Embedding cache unavailable; computing embedding without cache")]
    private static partial void LogCacheError(ILogger logger, Exception exception);
}
