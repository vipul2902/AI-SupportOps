using AISupportOps.Application.Documents;
using AISupportOps.Application.Identity;
using AISupportOps.Application.Ingestion;
using AISupportOps.Application.Knowledge;
using AISupportOps.Application.Tenants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AISupportOps.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<AuthService>();
        services.AddScoped<TeamService>();
        services.AddScoped<TenantService>();
        services.AddScoped<DocumentService>();
        services.AddScoped<DocumentIngestionService>();
        services.AddScoped<KnowledgeSearchService>();

        services.AddOptions<IngestionOptions>()
            .Bind(configuration.GetSection(IngestionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<DocumentOptions>()
            .Bind(configuration.GetSection(DocumentOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        return services;
    }
}
