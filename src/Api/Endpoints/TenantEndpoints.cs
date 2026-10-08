using AISupportOps.Api.Auth;
using AISupportOps.Application.Tenants;

namespace AISupportOps.Api.Endpoints;

internal static class TenantEndpoints
{
    public static IEndpointRouteBuilder MapTenantEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/me", (TenantService tenants, CancellationToken ct) => tenants.GetMeAsync(ct))
            .WithTags("Account")
            .RequireAuthorization(Policies.Viewer);

        var tenant = app.MapGroup("/api/tenant").WithTags("Organization");

        tenant.MapGet("/", (TenantService tenants, CancellationToken ct) => tenants.GetCurrentAsync(ct))
            .RequireAuthorization(Policies.Viewer);

        tenant.MapPatch("/", (RenameTenantRequest request, TenantService tenants, CancellationToken ct) =>
                tenants.RenameAsync(request, ct))
            .RequireAuthorization(Policies.Admin);

        var team = app.MapGroup("/api/team").WithTags("Team");

        team.MapGet("/members", (TeamService svc, CancellationToken ct) => svc.ListMembersAsync(ct))
            .RequireAuthorization(Policies.Viewer);

        team.MapPatch("/members/{userId:guid}/role", (Guid userId, ChangeRoleRequest request, TeamService svc, CancellationToken ct) =>
                svc.ChangeRoleAsync(userId, request.Role, ct))
            .RequireAuthorization(Policies.Admin);

        team.MapDelete("/members/{userId:guid}", async (Guid userId, TeamService svc, CancellationToken ct) =>
            {
                await svc.RemoveMemberAsync(userId, ct);
                return Results.NoContent();
            })
            .RequireAuthorization(Policies.Admin);

        team.MapGet("/invitations", (TeamService svc, CancellationToken ct) => svc.ListPendingInvitationsAsync(ct))
            .RequireAuthorization(Policies.Admin);

        team.MapPost("/invitations", async (CreateInvitationRequest request, TeamService svc, CancellationToken ct) =>
            {
                var created = await svc.InviteAsync(request, ct);
                return Results.Created($"/api/team/invitations/{created.Invitation.Id}", created);
            })
            .RequireAuthorization(Policies.Admin);

        team.MapDelete("/invitations/{invitationId:guid}", async (Guid invitationId, TeamService svc, CancellationToken ct) =>
            {
                await svc.RevokeInvitationAsync(invitationId, ct);
                return Results.NoContent();
            })
            .RequireAuthorization(Policies.Admin);

        return app;
    }
}
