using AISupportOps.Api.Auth;
using AISupportOps.Application.Chat;
using AISupportOps.Application.Evaluation;

namespace AISupportOps.Api.Endpoints;

internal static class EvaluationEndpoints
{
    public static IEndpointRouteBuilder MapEvaluationEndpoints(this IEndpointRouteBuilder app)
    {
        // Any user can rate answers in their own conversations.
        app.MapPost("/api/conversations/{conversationId:guid}/messages/{messageId:guid}/feedback",
                async (Guid conversationId, Guid messageId, FeedbackRequest request, ConversationService svc, CancellationToken ct) =>
                {
                    await svc.SetFeedbackAsync(conversationId, messageId, request, ct);
                    return Results.NoContent();
                })
            .WithTags("Evaluation")
            .RequireAuthorization(Policies.Viewer);

        var evaluations = app.MapGroup("/api/evaluations").WithTags("Evaluation").RequireAuthorization(Policies.Admin);

        evaluations.MapGet("/metrics", (AiMetricsService svc, CancellationToken ct, int days = 30) => svc.GetAsync(days, ct));

        // Runs the dataset through the live pipeline: costs tokens, so it is AI-rate-limited too.
        evaluations.MapPost("/runs", (RunEvaluationRequest request, EvaluationService svc, CancellationToken ct) => svc.RunAsync(request, ct))
            .RequireRedisRateLimit(RateLimitPolicies.Ai);

        evaluations.MapGet("/runs", (EvaluationService svc, CancellationToken ct) => svc.ListRunsAsync(ct));

        evaluations.MapGet("/runs/{id:guid}", (Guid id, EvaluationService svc, CancellationToken ct) => svc.GetRunAsync(id, ct));

        return app;
    }
}
