using AISupportOps.Api.Auth;
using AISupportOps.Application.Agents;

namespace AISupportOps.Api.Endpoints;

internal static class AgentEndpoints
{
    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        // Viewer+ may use the agent; which tools it gets is decided per call from the user's role.
        app.MapPost("/api/agent", (AgentRequest request, AgentService agent, CancellationToken ct) => agent.RunAsync(request, ct))
            .WithTags("Agent")
            .RequireAuthorization(Policies.Viewer)
            .RequireRedisRateLimit(RateLimitPolicies.Ai);

        return app;
    }
}
