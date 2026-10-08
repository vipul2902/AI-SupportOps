using AISupportOps.Api.Auth;
using AISupportOps.Application.Common;
using AISupportOps.Application.Identity;

namespace AISupportOps.Api.Endpoints;

internal static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth")
            .WithTags("Auth")
            .RequireRedisRateLimit(RateLimitPolicies.Auth);

        group.MapPost("/register", (RegisterRequest request, AuthService auth, CancellationToken ct) =>
                auth.RegisterAsync(request, ct))
            .AllowAnonymous();

        group.MapPost("/login", (LoginRequest request, AuthService auth, CancellationToken ct) =>
                auth.LoginAsync(request, ct))
            .AllowAnonymous();

        group.MapPost("/refresh", (RefreshRequest request, AuthService auth, CancellationToken ct) =>
                auth.RefreshAsync(request, ct))
            .AllowAnonymous();

        group.MapPost("/logout", async (RefreshRequest request, AuthService auth, CancellationToken ct) =>
            {
                await auth.LogoutAsync(request, ct);
                return Results.NoContent();
            })
            .AllowAnonymous();

        group.MapPost("/accept-invitation", (AcceptInvitationRequest request, AuthService auth, CancellationToken ct) =>
                auth.AcceptInvitationAsync(request, ct))
            .AllowAnonymous();

        group.MapPost("/switch-tenant", (SwitchTenantRequest request, AuthService auth, ICurrentUser user, CancellationToken ct) =>
                auth.SwitchTenantAsync(user.RequireUserId(), request.TenantId, ct))
            .RequireAuthorization(Policies.Viewer);

        return app;
    }
}
