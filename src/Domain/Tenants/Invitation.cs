using AISupportOps.Domain.Common;
using AISupportOps.Domain.Identity;

namespace AISupportOps.Domain.Tenants;

/// <summary>
/// A pending invitation to join a tenant. Only a hash of the invitation token is stored,
/// so a database leak does not expose usable invitation links.
/// </summary>
public sealed class Invitation : Entity, ITenantOwned
{
    private Invitation()
    {
    }

    public Invitation(Guid tenantId, string email, TenantRole role, string tokenHash, DateTimeOffset expiresAt, Guid invitedByUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        TenantId = tenantId;
        Email = email.Trim();
        NormalizedEmail = User.NormalizeEmail(email);
        Role = role;
        TokenHash = tokenHash;
        ExpiresAt = expiresAt;
        InvitedByUserId = invitedByUserId;
    }

    public Guid TenantId { get; private set; }

    public string Email { get; private set; } = string.Empty;

    public string NormalizedEmail { get; private set; } = string.Empty;

    public TenantRole Role { get; private set; }

    public string TokenHash { get; private set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? AcceptedAt { get; private set; }

    public Guid InvitedByUserId { get; private set; }

    public bool IsPending(DateTimeOffset now) => AcceptedAt is null && ExpiresAt > now;

    public void Accept(DateTimeOffset now)
    {
        if (!IsPending(now))
        {
            throw new InvalidOperationException("Invitation is no longer pending.");
        }

        AcceptedAt = now;
    }
}
