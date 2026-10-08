using AISupportOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace AISupportOps.IntegrationTests.Fixtures;

/// <summary>
/// Boots the real API against throwaway PostgreSQL (pgvector) and Redis containers.
/// Shared per test collection so containers start once; tests isolate data by using
/// unique emails/organizations rather than resetting the database.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string TestSigningKey = "integration-tests-signing-key-0123456789abcdef";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg17").Build();
    private readonly RedisContainer _redis = new RedisBuilder("redis:7-alpine").Build();

    public const long MaxUploadBytes = 64 * 1024;

    public string PostgresConnectionString => _postgres.GetConnectionString();

    public string StorageRoot { get; } = Path.Combine(Path.GetTempPath(), "aisupportops-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
        builder.UseSetting("ConnectionStrings:Redis", _redis.GetConnectionString());
        builder.UseSetting("Database:ApplyMigrationsOnStartup", "false");
        builder.UseSetting("Auth:SigningKey", TestSigningKey);
        builder.UseSetting("RateLimiting:AuthPermitsPerMinute", "10000");
        builder.UseSetting("Storage:Local:RootPath", StorageRoot);
        builder.UseSetting("Documents:MaxFileSizeBytes", MaxUploadBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());
        await DatabaseMigrator.MigrateAsync(Services);
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
        if (Directory.Exists(StorageRoot))
        {
            Directory.Delete(StorageRoot, recursive: true);
        }
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}
