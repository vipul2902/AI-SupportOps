using System.Linq.Expressions;
using AISupportOps.Application.Common;
using AISupportOps.Domain.Auditing;
using AISupportOps.Domain.Chat;
using AISupportOps.Domain.Common;
using AISupportOps.Domain.Documents;
using AISupportOps.Domain.Identity;
using AISupportOps.Domain.Tenants;
using AISupportOps.Domain.Tickets;
using AISupportOps.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace AISupportOps.Infrastructure.Persistence;

public sealed class AppDbContext(
    DbContextOptions<AppDbContext> options,
    ITenantContext tenantContext,
    TimeProvider timeProvider)
    : DbContext(options), IApplicationDbContext
{
    public DbSet<User> Users => Set<User>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<TenantMembership> TenantMemberships => Set<TenantMembership>();

    public DbSet<Invitation> Invitations => Set<Invitation>();

    public DbSet<Document> Documents => Set<Document>();

    public DbSet<DocumentChunk> DocumentChunks => Set<DocumentChunk>();

    public DbSet<Conversation> Conversations => Set<Conversation>();

    public DbSet<Message> Messages => Set<Message>();

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<SupportTicket> SupportTickets => Set<SupportTicket>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    internal DbSet<TicketCounter> TicketCounters => Set<TicketCounter>();

    /// <summary>
    /// Read by the global query filters on every query. EF Core evaluates it per DbContext
    /// instance (i.e. per request). Guid.Empty matches no rows, so "no tenant" fails closed.
    /// </summary>
    internal Guid CurrentTenantId => tenantContext.TenantId ?? Guid.Empty;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // pgvector powers semantic search over document chunks (Phase 5).
        modelBuilder.HasPostgresExtension("vector");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        ApplyTenantQueryFilters(modelBuilder);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        BeforeSave();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        BeforeSave();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <summary>Adds <c>WHERE tenant_id = @CurrentTenantId</c> to every query on a tenant-owned entity.</summary>
    private void ApplyTenantQueryFilters(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                     .Where(t => typeof(ITenantOwned).IsAssignableFrom(t.ClrType)))
        {
            var entity = Expression.Parameter(entityType.ClrType, "e");
            var tenantId = Expression.Property(entity, nameof(ITenantOwned.TenantId));
            var current = Expression.Property(Expression.Constant(this), nameof(CurrentTenantId));
            var filter = Expression.Lambda(Expression.Equal(tenantId, current), entity);

            modelBuilder.Entity(entityType.ClrType).HasQueryFilter(filter);
        }
    }

    private void BeforeSave()
    {
        GuardTenantWrites();
        StampTimestamps();
    }

    /// <summary>
    /// Defense in depth: even if a use case builds an entity with the wrong TenantId,
    /// it cannot be written while a different tenant is active.
    /// </summary>
    private void GuardTenantWrites()
    {
        var current = tenantContext.TenantId;
        foreach (var entry in ChangeTracker.Entries<ITenantOwned>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            var tenantId = entry.Entity.TenantId;
            if (tenantId == Guid.Empty)
            {
                throw new InvalidOperationException($"{entry.Metadata.ClrType.Name} must have a TenantId.");
            }

            if (current is not null && tenantId != current)
            {
                throw new InvalidOperationException(
                    $"Cross-tenant write blocked for {entry.Metadata.ClrType.Name}.");
            }
        }
    }

    private void StampTimestamps()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var entry in ChangeTracker.Entries<Entity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }
    }
}
