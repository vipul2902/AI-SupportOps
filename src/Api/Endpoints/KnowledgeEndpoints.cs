using AISupportOps.Api.Auth;
using AISupportOps.Application.Knowledge;

namespace AISupportOps.Api.Endpoints;

internal static class KnowledgeEndpoints
{
    public static IEndpointRouteBuilder MapKnowledgeEndpoints(this IEndpointRouteBuilder app)
    {
        // POST (not GET) so queries — which may contain customer details — stay out of URLs and access logs.
        app.MapPost("/api/search", (SearchRequest request, KnowledgeSearchService search, CancellationToken ct) =>
                search.SearchAsync(request, ct))
            .WithTags("Knowledge")
            .RequireAuthorization(Policies.Viewer)
            .RequireRedisRateLimit(RateLimitPolicies.Ai); // each cache miss is a paid embedding call

        app.MapPost("/api/ask", (AskRequest request, IRagService rag, CancellationToken ct) =>
                rag.AskAsync(request, ct))
            .WithTags("Knowledge")
            .RequireAuthorization(Policies.Viewer)
            .RequireRedisRateLimit(RateLimitPolicies.Ai);

        return app;
    }
}
