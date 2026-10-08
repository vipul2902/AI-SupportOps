using AISupportOps.Domain.Common;

namespace AISupportOps.Domain.Identity;

/// <summary>
/// A long-lived credential exchanged for new access tokens. Tokens rotate on every use;
/// all tokens descending from one login share a <see cref="FamilyId"/> so that reuse of a
/// rotated token (a sign of theft) can revoke the whole chain.
/// </summary>
public sealed class RefreshToken : Entity
{
    private RefreshToken()
    {
    }

    public RefreshToken(Guid userId, Guid tenantId, Guid familyId, string tokenHash, DateTimeOffset expiresAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        UserId = userId;
        TenantId = tenantId;
        FamilyId = familyId;
        TokenHash = tokenHash;
        ExpiresAt = expiresAt;
    }

    public Guid UserId { get; private set; }

    /// <summary>The tenant the session is scoped to. Not a query-filtered tenant-owned row.</summary>
    public Guid TenantId { get; private set; }

    public Guid FamilyId { get; private set; }

    public string TokenHash { get; private set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}
