using AISupportOps.Domain.Documents;
using AISupportOps.Domain.Identity;
using AISupportOps.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace AISupportOps.Application.Common;

/// <summary>
/// The Application layer's view of persistence. Use cases query with LINQ directly instead
/// of going through per-entity repositories; EF Core already is a unit of work + repository.
/// Tenant-owned sets are automatically filtered to the current tenant.
/// </summary>
public interface IApplicationDbContext
{
    DbSet<User> Users { get; }

    DbSet<RefreshToken> RefreshTokens { get; }

    DbSet<Tenant> Tenants { get; }

    DbSet<TenantMembership> TenantMemberships { get; }

    DbSet<Invitation> Invitations { get; }

    DbSet<Document> Documents { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
