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

        group.MapPost("/register", async (RegisterRequest request, AuthService auth, HttpContext http, CancellationToken ct) =>
                RefreshCookie.Issue(http, await auth.RegisterAsync(request, ct)))
            .AllowAnonymous();

        group.MapPost("/login", async (LoginRequest request, AuthService auth, HttpContext http, CancellationToken ct) =>
                RefreshCookie.Issue(http, await auth.LoginAsync(request, ct)))
            .AllowAnonymous();

        // Body optional: browsers in cookie mode send the token as an httpOnly cookie instead.
        group.MapPost("/refresh", async (RefreshRequest? request, AuthService auth, HttpContext http, CancellationToken ct) =>
            {
                try
                {
                    return RefreshCookie.Issue(http, await auth.RefreshAsync(RefreshCookie.Resolve(http, request), ct));
                }
                catch (UnauthorizedException)
                {
                    RefreshCookie.Clear(http); // a dead cookie should not be resent forever
                    throw;
                }
            })
            .AllowAnonymous();

        group.MapPost("/logout", async (RefreshRequest? request, AuthService auth, HttpContext http, CancellationToken ct) =>
            {
                await auth.LogoutAsync(RefreshCookie.Resolve(http, request), ct);
                RefreshCookie.Clear(http);
                return Results.NoContent();
            })
            .AllowAnonymous();

        group.MapPost("/accept-invitation", async (AcceptInvitationRequest request, AuthService auth, HttpContext http, CancellationToken ct) =>
                RefreshCookie.Issue(http, await auth.AcceptInvitationAsync(request, ct)))
            .AllowAnonymous();

        group.MapPost("/switch-tenant", async (SwitchTenantRequest request, AuthService auth, ICurrentUser user, HttpContext http, CancellationToken ct) =>
                RefreshCookie.Issue(http, await auth.SwitchTenantAsync(user.RequireUserId(), request.TenantId, ct)))
            .RequireAuthorization(Policies.Viewer);

        return app;
    }
}
