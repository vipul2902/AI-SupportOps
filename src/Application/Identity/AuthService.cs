using AISupportOps.Application.Common;
using AISupportOps.Domain.Identity;
using AISupportOps.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AISupportOps.Application.Identity;

/// <summary>
/// Sign-up, sign-in, token refresh/rotation, tenant switching, and invitation acceptance.
/// These flows run before a tenant is selected, so membership lookups explicitly bypass
/// the tenant query filter and constrain by user instead.
/// </summary>
public sealed class AuthService(
    IApplicationDbContext db,
    IPasswordHasher passwordHasher,
    IAccessTokenIssuer accessTokenIssuer,
    ITenantScope tenantScope,
    IOptions<AuthOptions> options,
    TimeProvider time)
{
    private const string InvalidCredentials = "Invalid email or password.";
    private const string InvalidRefreshToken = "Invalid or expired refresh token.";

    // Hash verified when the email does not exist, so response time doesn't reveal which emails are registered.
    private static string? s_timingDummyHash;

    private readonly AuthOptions _options = options.Value;

    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        var normalizedEmail = User.NormalizeEmail(request.Email);
        if (await db.Users.AnyAsync(u => u.NormalizedEmail == normalizedEmail, ct))
        {
            throw new ConflictException("An account with this email already exists.");
        }

        var user = new User(request.Email, request.DisplayName);
        user.SetPasswordHash(passwordHasher.Hash(user, request.Password));

        var tenant = new Tenant(request.OrganizationName, Slug.Create(request.OrganizationName));
        var membership = new TenantMembership(tenant.Id, user.Id, TenantRole.Owner);

        // The caller may already be signed in to another tenant; this write targets the new one.
        using var scope = tenantScope.Enter(tenant.Id);

        db.Users.Add(user);
        db.Tenants.Add(tenant);
        db.TenantMemberships.Add(membership);

        return await IssueTokensAsync(user, membership, familyId: Guid.CreateVersion7(), ct);
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var normalizedEmail = User.NormalizeEmail(request.Email);
        var user = await db.Users.SingleOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail, ct);

        if (user is null)
        {
            var dummy = new User("timing@invalid", "timing");
            dummy.SetPasswordHash(s_timingDummyHash ??= passwordHasher.Hash(dummy, SecureToken.Create()));
            passwordHasher.Verify(dummy, request.Password);
            throw new UnauthorizedException(InvalidCredentials);
        }

        if (!passwordHasher.Verify(user, request.Password))
        {
            throw new UnauthorizedException(InvalidCredentials);
        }

        var memberships = MembershipsOf(user.Id);
        var membership = request.TenantId is { } tenantId
            ? await memberships.SingleOrDefaultAsync(m => m.TenantId == tenantId, ct)
                ?? throw new ForbiddenException("You are not a member of that organization.")
            : await memberships.OrderBy(m => m.CreatedAt).FirstOrDefaultAsync(ct)
                ?? throw new ForbiddenException("You are not a member of any organization.");

        return await IssueTokensAsync(user, membership, familyId: Guid.CreateVersion7(), ct);
    }

    public async Task<AuthResponse> RefreshAsync(RefreshRequest request, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var hash = SecureToken.Hash(request.RefreshToken);
        var token = await db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, ct)
            ?? throw new UnauthorizedException(InvalidRefreshToken);

        if (token.RevokedAt is not null)
        {
            // A rotated token was presented again: assume it was stolen and kill the whole session chain.
            await RevokeFamilyAsync(token.FamilyId, now, ct);
            throw new UnauthorizedException(InvalidRefreshToken);
        }

        if (!token.IsActive(now))
        {
            throw new UnauthorizedException(InvalidRefreshToken);
        }

        // Re-check membership so removed users cannot keep refreshing, and role changes apply.
        var membership = await MembershipsOf(token.UserId).SingleOrDefaultAsync(m => m.TenantId == token.TenantId, ct);
        if (membership is null)
        {
            token.Revoke(now);
            await db.SaveChangesAsync(ct);
            throw new UnauthorizedException(InvalidRefreshToken);
        }

        token.Revoke(now);
        var user = await db.Users.SingleAsync(u => u.Id == token.UserId, ct);
        return await IssueTokensAsync(user, membership, token.FamilyId, ct);
    }

    public async Task LogoutAsync(RefreshRequest request, CancellationToken ct)
    {
        var hash = SecureToken.Hash(request.RefreshToken);
        var token = await db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is not null)
        {
            await RevokeFamilyAsync(token.FamilyId, time.GetUtcNow(), ct);
        }
    }

    public async Task<AuthResponse> SwitchTenantAsync(Guid userId, Guid tenantId, CancellationToken ct)
    {
        var membership = await MembershipsOf(userId).SingleOrDefaultAsync(m => m.TenantId == tenantId, ct)
            ?? throw new ForbiddenException("You are not a member of that organization.");
        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);

        return await IssueTokensAsync(user, membership, familyId: Guid.CreateVersion7(), ct);
    }

    /// <summary>
    /// Anonymous endpoint: possession of the invitation token proves the invite. New users set a
    /// password here; existing users must prove ownership of the account with their password.
    /// </summary>
    public async Task<AuthResponse> AcceptInvitationAsync(AcceptInvitationRequest request, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var hash = SecureToken.Hash(request.Token);
        var invitation = await db.Invitations
            .IgnoreQueryFilters() // No tenant is selected yet; the token hash identifies exactly one row.
            .SingleOrDefaultAsync(i => i.TokenHash == hash, ct);

        if (invitation is null || !invitation.IsPending(now))
        {
            throw new NotFoundException("Invitation is invalid or has expired.");
        }

        var user = await db.Users.SingleOrDefaultAsync(u => u.NormalizedEmail == invitation.NormalizedEmail, ct);
        if (user is null)
        {
            if (string.IsNullOrWhiteSpace(request.DisplayName))
            {
                throw new BusinessRuleException("Display name is required to create a new account.");
            }

            user = new User(invitation.Email, request.DisplayName);
            user.SetPasswordHash(passwordHasher.Hash(user, request.Password));
            db.Users.Add(user);
        }
        else if (!passwordHasher.Verify(user, request.Password))
        {
            throw new UnauthorizedException(InvalidCredentials);
        }

        // Possession of the invitation authorizes acting on its tenant, whatever tenant the caller's token is for.
        using var scope = tenantScope.Enter(invitation.TenantId);

        if (await MembershipsOf(user.Id).AnyAsync(m => m.TenantId == invitation.TenantId, ct))
        {
            throw new ConflictException("You are already a member of this organization.");
        }

        invitation.Accept(now);
        var membership = new TenantMembership(invitation.TenantId, user.Id, invitation.Role);
        db.TenantMemberships.Add(membership);

        return await IssueTokensAsync(user, membership, familyId: Guid.CreateVersion7(), ct);
    }

    private IQueryable<TenantMembership> MembershipsOf(Guid userId) =>
        db.TenantMemberships.IgnoreQueryFilters().Where(m => m.UserId == userId);

    private async Task RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken ct)
    {
        var active = await db.RefreshTokens.Where(t => t.FamilyId == familyId && t.RevokedAt == null).ToListAsync(ct);
        foreach (var t in active)
        {
            t.Revoke(now);
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<AuthResponse> IssueTokensAsync(User user, TenantMembership membership, Guid familyId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var refreshToken = SecureToken.Create();
        var refreshExpiresAt = now + _options.RefreshTokenLifetime;

        db.RefreshTokens.Add(new RefreshToken(user.Id, membership.TenantId, familyId, SecureToken.Hash(refreshToken), refreshExpiresAt));
        await db.SaveChangesAsync(ct);

        var access = accessTokenIssuer.Issue(user, membership.TenantId, membership.Role);
        return new AuthResponse(access.Token, access.ExpiresAt, refreshToken, refreshExpiresAt, membership.TenantId, membership.Role);
    }
}
