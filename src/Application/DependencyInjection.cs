using AISupportOps.Application.Agents;
using AISupportOps.Application.Agents.Tools;
using AISupportOps.Application.Auditing;
using AISupportOps.Application.Chat;
using AISupportOps.Application.Documents;
using AISupportOps.Application.Identity;
using AISupportOps.Application.Ingestion;
using AISupportOps.Application.Knowledge;
using AISupportOps.Application.Tenants;
using AISupportOps.Application.Tickets;
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
        services.AddScoped<IRagService, RagService>();
        services.AddScoped<QueryRewriter>();
        services.AddScoped<ConversationSummarizer>();
        services.AddScoped<ChatService>();
        services.AddScoped<ConversationService>();
        services.AddScoped<AuditTrail>();
        services.AddScoped<AuditLogService>();
        services.AddScoped<TicketService>();
        services.AddScoped<CustomerService>();

        // Agent: register a tool here and it is available (subject to its MinimumRole); nothing else changes.
        services.AddScoped<IAgentTool, SearchKnowledgeBaseTool>();
        services.AddScoped<IAgentTool, GetSupportTicketTool>();
        services.AddScoped<IAgentTool, CreateSupportTicketTool>();
        services.AddScoped<IAgentTool, UpdateSupportTicketTool>();
        services.AddScoped<IAgentTool, GetCustomerInformationTool>();
        services.AddScoped<ToolRegistry>();
        services.AddScoped<ToolExecutor>();
        services.AddScoped<AgentService>();
        services.AddOptions<AgentOptions>()
            .Bind(configuration.GetSection(AgentOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<ConversationOptions>()
            .Bind(configuration.GetSection(ConversationOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<RagOptions>()
            .Bind(configuration.GetSection(RagOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

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
