using AISupportOps.Application.Common;
using AISupportOps.Application.Identity;
using AISupportOps.Domain.Identity;
using AISupportOps.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AISupportOps.Application.Tenants;

/// <summary>
/// Member and invitation management within the current tenant. All queries rely on the
/// tenant query filter; authority is re-checked against the database rather than trusting
/// the (up to 15-minute-old) role claim in the access token.
/// </summary>
public sealed class TeamService(
    IApplicationDbContext db,
    ICurrentUser currentUser,
    IOptions<AuthOptions> options,
    TimeProvider time)
{
    public async Task<IReadOnlyList<MemberResponse>> ListMembersAsync(CancellationToken ct) =>
        await db.TenantMemberships
            .OrderBy(m => m.CreatedAt)
            .Select(m => new MemberResponse(m.UserId, m.User.Email, m.User.DisplayName, m.Role, m.CreatedAt))
            .ToListAsync(ct);

    public async Task<MemberResponse> ChangeRoleAsync(Guid userId, TenantRole role, CancellationToken ct)
    {
        var actorRole = await GetActorRoleAsync(ct);
        var membership = await FindMemberAsync(userId, ct);

        if (!RolePolicy.CanChangeRole(actorRole, membership.Role, role))
        {
            throw new ForbiddenException("You are not allowed to make this role change.");
        }

        if (membership.Role == TenantRole.Owner && role != TenantRole.Owner)
        {
            await EnsureAnotherOwnerExistsAsync(ct);
        }

        membership.ChangeRole(role);
        await db.SaveChangesAsync(ct);

        return new MemberResponse(membership.UserId, membership.User.Email, membership.User.DisplayName, membership.Role, membership.CreatedAt);
    }

    public async Task RemoveMemberAsync(Guid userId, CancellationToken ct)
    {
        var actorRole = await GetActorRoleAsync(ct);
        var membership = await FindMemberAsync(userId, ct);

        if (!RolePolicy.CanRemove(actorRole, membership.Role))
        {
            throw new ForbiddenException("You are not allowed to remove this member.");
        }

        if (membership.Role == TenantRole.Owner)
        {
            await EnsureAnotherOwnerExistsAsync(ct);
        }

        db.TenantMemberships.Remove(membership);

        // End the removed user's sessions for this tenant immediately.
        var now = time.GetUtcNow();
        var tenantId = currentUser.RequireTenantId();
        var sessions = await db.RefreshTokens
            .Where(t => t.UserId == userId && t.TenantId == tenantId && t.RevokedAt == null)
            .ToListAsync(ct);
        sessions.ForEach(t => t.Revoke(now));

        await db.SaveChangesAsync(ct);
    }

    public async Task<InvitationCreatedResponse> InviteAsync(CreateInvitationRequest request, CancellationToken ct)
    {
        var actorRole = await GetActorRoleAsync(ct);
        if (!RolePolicy.CanManageMembers(actorRole) || !RolePolicy.CanAssignRole(actorRole, request.Role))
        {
            throw new ForbiddenException("You are not allowed to invite a member with this role.");
        }

        var normalizedEmail = User.NormalizeEmail(request.Email);
        if (await db.TenantMemberships.AnyAsync(m => m.User.NormalizedEmail == normalizedEmail, ct))
        {
            throw new ConflictException("This person is already a member.");
        }

        // A new invitation supersedes any outstanding one for the same email.
        var now = time.GetUtcNow();
        var outstanding = await db.Invitations
            .Where(i => i.NormalizedEmail == normalizedEmail && i.AcceptedAt == null)
            .ToListAsync(ct);
        db.Invitations.RemoveRange(outstanding);

        var token = SecureToken.Create();
        var invitation = new Invitation(
            currentUser.RequireTenantId(),
            request.Email,
            request.Role,
            SecureToken.Hash(token),
            now + options.Value.InvitationLifetime,
            currentUser.RequireUserId());

        db.Invitations.Add(invitation);
        await db.SaveChangesAsync(ct);

        return new InvitationCreatedResponse(ToResponse(invitation), token);
    }

    public async Task<IReadOnlyList<InvitationResponse>> ListPendingInvitationsAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return await db.Invitations
            .Where(i => i.AcceptedAt == null && i.ExpiresAt > now)
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new InvitationResponse(i.Id, i.Email, i.Role, i.ExpiresAt, i.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task RevokeInvitationAsync(Guid invitationId, CancellationToken ct)
    {
        var actorRole = await GetActorRoleAsync(ct);
        if (!RolePolicy.CanManageMembers(actorRole))
        {
            throw new ForbiddenException("You are not allowed to revoke invitations.");
        }

        var invitation = await db.Invitations.SingleOrDefaultAsync(i => i.Id == invitationId, ct)
            ?? throw new NotFoundException("Invitation not found.");

        db.Invitations.Remove(invitation);
        await db.SaveChangesAsync(ct);
    }

    private async Task<TenantRole> GetActorRoleAsync(CancellationToken ct)
    {
        var actorId = currentUser.RequireUserId();
        var role = await db.TenantMemberships
            .Where(m => m.UserId == actorId)
            .Select(m => (TenantRole?)m.Role)
            .SingleOrDefaultAsync(ct);

        return role ?? throw new ForbiddenException("You are no longer a member of this organization.");
    }

    private async Task<TenantMembership> FindMemberAsync(Guid userId, CancellationToken ct) =>
        await db.TenantMemberships.Include(m => m.User).SingleOrDefaultAsync(m => m.UserId == userId, ct)
            ?? throw new NotFoundException("Member not found.");

    private async Task EnsureAnotherOwnerExistsAsync(CancellationToken ct)
    {
        var owners = await db.TenantMemberships.CountAsync(m => m.Role == TenantRole.Owner, ct);
        if (owners <= 1)
        {
            throw new BusinessRuleException("An organization must have at least one owner.");
        }
    }

    private static InvitationResponse ToResponse(Invitation i) =>
        new(i.Id, i.Email, i.Role, i.ExpiresAt, i.CreatedAt);
}
