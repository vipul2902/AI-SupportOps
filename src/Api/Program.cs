using System.Text.Json.Serialization;
using AISupportOps.Api.Auth;
using AISupportOps.Api.Endpoints;
using AISupportOps.Api.Infrastructure;
using AISupportOps.Application;
using AISupportOps.Application.Documents;
using AISupportOps.Infrastructure;
using AISupportOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);
builder.AddObservability();

builder.Services.AddOpenApi();
builder.Services.AddValidation();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = ctx =>
        ctx.ProblemDetails.Extensions["traceId"] = Observability.CurrentTraceId(ctx.HttpContext));
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.Services.AddApplication(builder.Configuration);

// Multipart limit slightly above the document limit (form overhead); the exact limit is
// enforced while streaming in DocumentService.
var maxUploadBytes = builder.Configuration.GetValue($"{DocumentOptions.SectionName}:MaxFileSizeBytes", new DocumentOptions().MaxFileSizeBytes);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = maxUploadBytes + (1024 * 1024));
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = maxUploadBytes + (1024 * 1024));
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApiAuth(builder.Configuration);
builder.Services.AddTrustedForwardedHeaders(builder.Configuration);
SecurityMiddleware.ValidateForwardedHeadersConfiguration(builder.Configuration, builder.Environment);

var app = builder.Build();

// First: everything after this sees the real client IP and scheme.
app.UseForwardedHeaders();
app.UseSecurityHeaders(app.Environment);
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();

if (app.Configuration.GetValue<bool>("Database:ApplyMigrationsOnStartup"))
{
    await DatabaseMigrator.MigrateAsync(app.Services);
}

// Liveness: is the process up? Readiness: can it reach its dependencies?
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(AISupportOps.Infrastructure.DependencyInjection.ReadyTag),
});

app.MapAuthEndpoints();
app.MapTenantEndpoints();
app.MapDocumentEndpoints();
app.MapKnowledgeEndpoints();
app.MapChatEndpoints();
app.MapTicketEndpoints();
app.MapAgentEndpoints();
app.MapEvaluationEndpoints();

await app.RunAsync();

/// <summary>Exposed for WebApplicationFactory in integration tests.</summary>
public partial class Program;
