using AISupportOps.Application.Common;
using AISupportOps.Application.Ai;
using AISupportOps.Application.Documents;
using AISupportOps.Application.Identity;
using AISupportOps.Application.Ingestion;
using AISupportOps.Application.Knowledge;
using AISupportOps.Domain.Documents;
using AISupportOps.Infrastructure.Ai;
using AISupportOps.Infrastructure.Identity;
using AISupportOps.Infrastructure.Ingestion;
using AISupportOps.Infrastructure.Knowledge;
using AISupportOps.Infrastructure.Persistence;
using AISupportOps.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;

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

        services.AddSingleton<ITokenCounter, TiktokenTokenCounter>();
        services.AddSingleton<ITextExtractor>(new PlainTextExtractor(DocumentKind.PlainText));
        services.AddSingleton<ITextExtractor>(new PlainTextExtractor(DocumentKind.Markdown));
        services.AddSingleton<ITextExtractor, PdfTextExtractor>();
        services.AddSingleton<ITextExtractor, DocxTextExtractor>();
        if (configuration.GetValue($"{IngestionOptions.SectionName}:{nameof(IngestionOptions.WorkerEnabled)}", true))
        {
            services.AddHostedService<DocumentIngestionWorker>();
        }

        services.AddAi(configuration);

        services.AddHealthChecks()
            .AddDbContextCheck<AppDbContext>("postgres", tags: [ReadyTag])
            .AddRedis(redis, "redis", tags: [ReadyTag]);

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
            if (ai.Provider == AiProvider.Fake)
            {
                return new FakeEmbeddingGenerator();
            }

            var client = new OpenAIClient(
                new System.ClientModel.ApiKeyCredential(ai.OpenAI.ApiKey!),
                new OpenAIClientOptions { Endpoint = ai.OpenAI.Endpoint });
            return client.GetEmbeddingClient(ai.OpenAI.EmbeddingModel).AsIEmbeddingGenerator();
        });

        services.AddSingleton<IEmbeddingService>(sp =>
        {
            var ai = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            var model = ai.Provider == AiProvider.Fake ? FakeEmbeddingGenerator.ModelId : ai.OpenAI.EmbeddingModel;
            return new EmbeddingService(
                sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
                model,
                sp.GetRequiredService<ILogger<EmbeddingService>>());
        });

        services.AddSingleton<IChatClient>(sp =>
        {
            var ai = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            if (ai.Provider == AiProvider.Fake)
            {
                return new FakeChatClient();
            }

            var client = new OpenAIClient(
                new System.ClientModel.ApiKeyCredential(ai.OpenAI.ApiKey!),
                new OpenAIClientOptions { Endpoint = ai.OpenAI.Endpoint });
            return client.GetChatClient(ai.OpenAI.ChatModel).AsIChatClient();
        });

        services.AddSingleton<IAiChatService>(sp =>
        {
            var ai = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            var model = ai.Provider == AiProvider.Fake ? FakeChatClient.ModelId : ai.OpenAI.ChatModel;
            return new AiChatService(
                sp.GetRequiredService<IChatClient>(),
                model,
                ai.OpenAI.RequestTimeout,
                sp.GetRequiredService<ILogger<AiChatService>>());
        });

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
