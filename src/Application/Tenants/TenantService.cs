using AISupportOps.Application.Common;
using AISupportOps.Application.Identity;
using Microsoft.EntityFrameworkCore;

namespace AISupportOps.Application.Tenants;

/// <summary>Current organization details and the caller's profile.</summary>
public sealed class TenantService(IApplicationDbContext db, ICurrentUser currentUser)
{
    public async Task<TenantResponse> GetCurrentAsync(CancellationToken ct)
    {
        var tenantId = currentUser.RequireTenantId();
        return await db.Tenants
            .Where(t => t.Id == tenantId)
            .Select(t => new TenantResponse(t.Id, t.Name, t.Slug, t.CreatedAt))
            .SingleOrDefaultAsync(ct)
            ?? throw new NotFoundException("Organization not found.");
    }

    public async Task<TenantResponse> RenameAsync(RenameTenantRequest request, CancellationToken ct)
    {
        var tenantId = currentUser.RequireTenantId();
        var tenant = await db.Tenants.SingleOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw new NotFoundException("Organization not found.");

        tenant.Rename(request.Name);
        await db.SaveChangesAsync(ct);

        return new TenantResponse(tenant.Id, tenant.Name, tenant.Slug, tenant.CreatedAt);
    }

    public async Task<MeResponse> GetMeAsync(CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        var tenantId = currentUser.RequireTenantId();

        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new UnauthorizedException("Account no longer exists.");

        // Deliberately cross-tenant: a user may see the list of organizations they belong to.
        var memberships = await db.TenantMemberships
            .IgnoreQueryFilters()
            .Where(m => m.UserId == userId)
            .OrderBy(m => m.Tenant.Name)
            .Select(m => new MembershipSummary(m.TenantId, m.Tenant.Name, m.Role))
            .ToListAsync(ct);

        var current = memberships.SingleOrDefault(m => m.TenantId == tenantId)
            ?? throw new ForbiddenException("You are no longer a member of this organization.");

        return new MeResponse(user.Id, user.Email, user.DisplayName, tenantId, current.Role, memberships);
    }
}
