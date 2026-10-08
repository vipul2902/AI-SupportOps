using AISupportOps.Application.Common;
using AISupportOps.Application.Knowledge;
using AISupportOps.Infrastructure.Caching;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AISupportOps.UnitTests.Caching;

public class CachingEmbeddingServiceTests
{
    private sealed class CountingEmbedder : IEmbeddingService
    {
        public int QueryCalls { get; private set; }

        public string ModelId => "test-model";

        public Task<EmbeddingBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct) =>
            Task.FromResult(new EmbeddingBatch(inputs.Select(_ => (float[])[1f, 2f]).ToList(), null));

        public Task<float[]> EmbedQueryAsync(string query, CancellationToken ct)
        {
            QueryCalls++;
            return Task.FromResult(new[] { query.Length, 0.5f, -1.25f });
        }
    }

    private sealed class Tenant(Guid? id) : ITenantContext
    {
        public Guid? TenantId => id;
    }

    private sealed class BrokenCache : IDistributedCache
    {
        public byte[]? Get(string key) => throw new TimeoutException("redis down");

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => throw new TimeoutException("redis down");

        public void Refresh(string key) => throw new TimeoutException();

        public Task RefreshAsync(string key, CancellationToken token = default) => throw new TimeoutException();

        public void Remove(string key) => throw new TimeoutException();

        public Task RemoveAsync(string key, CancellationToken token = default) => throw new TimeoutException();

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw new TimeoutException();

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => throw new TimeoutException();
    }

    private static MemoryDistributedCache NewCache() => new(Options.Create(new MemoryDistributedCacheOptions()));

    [Fact]
    public async Task Second_identical_query_is_served_from_cache_with_identical_vector()
    {
        var inner = new CountingEmbedder();
        var sut = new CachingEmbeddingService(inner, NewCache(), new Tenant(Guid.NewGuid()), NullLogger<CachingEmbeddingService>.Instance);

        var first = await sut.EmbedQueryAsync("reset password", CancellationToken.None);
        var second = await sut.EmbedQueryAsync("reset password", CancellationToken.None);

        Assert.Equal(1, inner.QueryCalls);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Cache_is_partitioned_by_tenant()
    {
        var inner = new CountingEmbedder();
        var cache = NewCache();
        var a = new CachingEmbeddingService(inner, cache, new Tenant(Guid.NewGuid()), NullLogger<CachingEmbeddingService>.Instance);
        var b = new CachingEmbeddingService(inner, cache, new Tenant(Guid.NewGuid()), NullLogger<CachingEmbeddingService>.Instance);

        await a.EmbedQueryAsync("same question", CancellationToken.None);
        await b.EmbedQueryAsync("same question", CancellationToken.None);

        Assert.Equal(2, inner.QueryCalls);
    }

    [Fact]
    public async Task Cache_failure_fails_open()
    {
        var inner = new CountingEmbedder();
        var sut = new CachingEmbeddingService(inner, new BrokenCache(), new Tenant(Guid.NewGuid()), NullLogger<CachingEmbeddingService>.Instance);

        var vector = await sut.EmbedQueryAsync("anything", CancellationToken.None);

        Assert.Equal(8f, vector[0]);
    }

    [Fact]
    public async Task Document_embeddings_bypass_the_cache()
    {
        var sut = new CachingEmbeddingService(new CountingEmbedder(), new BrokenCache(), new Tenant(Guid.NewGuid()), NullLogger<CachingEmbeddingService>.Instance);

        var batch = await sut.EmbedAsync(["a", "b"], CancellationToken.None); // BrokenCache would throw if touched

        Assert.Equal(2, batch.Vectors.Count);
    }

    [Fact]
    public void Keys_contain_tenant_and_model_but_never_the_raw_query()
    {
        var tenant = Guid.NewGuid();

        var key = CachingEmbeddingService.Key(tenant, "text-embedding-3-small", "my account number is 12345");

        Assert.StartsWith($"emb:{tenant:N}:text-embedding-3-small:", key, StringComparison.Ordinal);
        Assert.DoesNotContain("12345", key, StringComparison.Ordinal);
    }
}
