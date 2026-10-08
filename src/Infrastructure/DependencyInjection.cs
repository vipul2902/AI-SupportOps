using AISupportOps.Application.Common;
using AISupportOps.Application.Ai;
using AISupportOps.Application.Documents;
using AISupportOps.Application.Identity;
using AISupportOps.Application.Ingestion;
using AISupportOps.Application.Knowledge;
using AISupportOps.Application.Tickets;
using AISupportOps.Domain.Documents;
using AISupportOps.Infrastructure.Ai;
using AISupportOps.Infrastructure.Caching;
using AISupportOps.Infrastructure.Identity;
using AISupportOps.Infrastructure.Ingestion;
using AISupportOps.Infrastructure.Knowledge;
using AISupportOps.Infrastructure.Persistence;
using AISupportOps.Infrastructure.Storage;
using AISupportOps.Infrastructure.Tickets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using StackExchange.Redis;

namespace AISupportOps.Infrastructure;

public static class DependencyInjection
{
    public const string PostgresConnectionName = "Postgres";
    public const string RedisConnectionName = "Redis";
    public const string ReadyTag = "ready";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var postgres = RequireConnectionString(configuration, PostgresConnectionName);
        var redis = RequireConnectionString(configuration, RedisConnectionName);

        services.AddSingleton(TimeProvider.System);

        services.AddOptions<AuthOptions>()
            .Bind(configuration.GetSection(AuthOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(postgres, npgsql => npgsql.UseVector()).UseSnakeCaseNamingConvention());
        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        services.AddSingleton<IPasswordHasher, PasswordHasher>();
        services.AddSingleton<IAccessTokenIssuer, JwtAccessTokenIssuer>();

        services.AddOptions<LocalFileStorageOptions>()
            .Bind(configuration.GetSection(LocalFileStorageOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IFileStorage, LocalFileStorage>();
        services.AddScoped<ITicketNumberGenerator, TicketNumberGenerator>();

        services.AddSingleton<ITokenCounter, TiktokenTokenCounter>();
        services.AddSingleton<ITextExtractor>(new PlainTextExtractor(DocumentKind.PlainText));
        services.AddSingleton<ITextExtractor>(new PlainTextExtractor(DocumentKind.Markdown));
        services.AddSingleton<ITextExtractor, PdfTextExtractor>();
        services.AddSingleton<ITextExtractor, DocxTextExtractor>();
        if (configuration.GetValue($"{IngestionOptions.SectionName}:{nameof(IngestionOptions.WorkerEnabled)}", true))
        {
            services.AddHostedService<DocumentIngestionWorker>();
        }

        // One shared, thread-safe multiplexer for the whole process (the documented StackExchange.Redis usage).
        // AbortOnConnectFail=false: start even if Redis is briefly unavailable; features fail open.
        var redisOptions = ConfigurationOptions.Parse(redis);
        redisOptions.AbortOnConnectFail = false;
        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisOptions));
        services.AddStackExchangeRedisCache(o => o.InstanceName = "aisupportops:");
        services.AddOptions<Microsoft.Extensions.Caching.StackExchangeRedis.RedisCacheOptions>()
            .Configure<IConnectionMultiplexer>((o, mux) => o.ConnectionMultiplexerFactory = () => Task.FromResult(mux));
        services.AddSingleton<IRateLimiter, RedisRateLimiter>();
        services.AddSingleton<ILoginThrottle, RedisLoginThrottle>();

        services.AddAi(configuration);

        services.AddHealthChecks()
            .AddDbContextCheck<AppDbContext>("postgres", tags: [ReadyTag])
            .AddRedis(sp => sp.GetRequiredService<IConnectionMultiplexer>(), "redis", tags: [ReadyTag]);

        return services;
    }

    private static void AddAi(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AiOptions>()
            .Bind(configuration.GetSection(AiOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<RetrievalOptions>().Bind(configuration.GetSection(RetrievalOptions.SectionName));

        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
        {
            var ai = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            IEmbeddingGenerator<string, Embedding<float>> generator = ai.Provider == AiProvider.Fake
                ? new FakeEmbeddingGenerator()
                : new OpenAIClient(
                        new System.ClientModel.ApiKeyCredential(ai.OpenAI.ApiKey!),
                        new OpenAIClientOptions { Endpoint = ai.OpenAI.Endpoint })
                    .GetEmbeddingClient(ai.OpenAI.EmbeddingModel).AsIEmbeddingGenerator();

            // GenAI semantic-convention spans/metrics (model, tokens, duration) for every embedding call.
            return generator.AsBuilder()
                .UseOpenTelemetry(sp.GetRequiredService<ILoggerFactory>(), Telemetry.AiName)
                .Build(sp);
        });

        services.AddSingleton(sp =>
        {
            var ai = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            var model = ai.Provider == AiProvider.Fake ? FakeEmbeddingGenerator.ModelId : ai.OpenAI.EmbeddingModel;
            return new EmbeddingService(
                sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
                model,
                sp.GetRequiredService<ILogger<EmbeddingService>>());
        });

        // Decorator: scoped because cache keys include the request's tenant.
        services.AddScoped<IEmbeddingService>(sp => new CachingEmbeddingService(
            sp.GetRequiredService<EmbeddingService>(),
            sp.GetRequiredService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>(),
            sp.GetRequiredService<ITenantContext>(),
            sp.GetRequiredService<ILogger<CachingEmbeddingService>>()));

        services.AddSingleton<IChatClient>(sp =>
        {
            var ai = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            IChatClient client = ai.Provider == AiProvider.Fake
                ? new FakeChatClient()
                : new OpenAIClient(
                        new System.ClientModel.ApiKeyCredential(ai.OpenAI.ApiKey!),
                        new OpenAIClientOptions { Endpoint = ai.OpenAI.Endpoint })
                    .GetChatClient(ai.OpenAI.ChatModel).AsIChatClient();

            // GenAI spans/metrics for every LLM call. EnableSensitiveData stays false (the default):
            // prompts and completions contain customer data and must not land in telemetry.
            return client.AsBuilder()
                .UseOpenTelemetry(sp.GetRequiredService<ILoggerFactory>(), Telemetry.AiName)
                .Build(sp);
        });

        services.AddSingleton(sp =>
        {
            var ai = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            var model = ai.Provider == AiProvider.Fake ? FakeChatClient.ModelId : ai.OpenAI.ChatModel;
            return new AiChatService(
                sp.GetRequiredService<IChatClient>(),
                model,
                ai.OpenAI.RequestTimeout,
                sp.GetRequiredService<ILogger<AiChatService>>());
        });
        services.AddSingleton<IAiChatService>(sp => sp.GetRequiredService<AiChatService>());
        services.AddSingleton<IAiToolChatService>(sp => sp.GetRequiredService<AiChatService>());

        services.AddScoped<IRetrievalService, PgvectorRetrievalService>();
    }

    private static string RequireConnectionString(IConfiguration configuration, string name)
    {
        var value = configuration.GetConnectionString(name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Connection string '{name}' is not configured.")
            : value;
    }
}
