using AISupportOps.Domain.Common;
using AISupportOps.Domain.Identity;

namespace AISupportOps.Domain.Tenants;

/// <summary>Links a user to a tenant with a role. A user may belong to many tenants.</summary>
public sealed class TenantMembership : Entity, ITenantOwned
{
    private TenantMembership()
    {
    }

    public TenantMembership(Guid tenantId, Guid userId, TenantRole role)
    {
        TenantId = tenantId;
        UserId = userId;
        Role = role;
    }

    public Guid TenantId { get; private set; }

    public Guid UserId { get; private set; }

    public TenantRole Role { get; private set; }

    public Tenant Tenant { get; private set; } = null!;

    public User User { get; private set; } = null!;

    public void ChangeRole(TenantRole role) => Role = role;
}
