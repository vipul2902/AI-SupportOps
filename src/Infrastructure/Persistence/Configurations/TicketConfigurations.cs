using AISupportOps.Domain.Auditing;
using AISupportOps.Domain.Identity;
using AISupportOps.Domain.Tenants;
using AISupportOps.Domain.Tickets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AISupportOps.Infrastructure.Persistence.Configurations;

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.Property(c => c.Name).HasMaxLength(Customer.NameMaxLength).IsRequired();
        builder.Property(c => c.Email).HasMaxLength(User.EmailMaxLength).IsRequired();
        builder.Property(c => c.NormalizedEmail).HasMaxLength(User.EmailMaxLength).IsRequired();
        builder.Property(c => c.Company).HasMaxLength(Customer.NameMaxLength);

        builder.HasIndex(c => new { c.TenantId, c.NormalizedEmail }).IsUnique();
        builder.HasIndex(c => new { c.TenantId, c.Name });

        builder.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SupportTicketConfiguration : IEntityTypeConfiguration<SupportTicket>
{
    public void Configure(EntityTypeBuilder<SupportTicket> builder)
    {
        builder.ToTable("support_tickets");
        builder.Property(t => t.Title).HasMaxLength(SupportTicket.TitleMaxLength).IsRequired();
        builder.Property(t => t.Description).HasMaxLength(SupportTicket.DescriptionMaxLength).IsRequired();
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.Priority).HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.Source).HasConversion<string>().HasMaxLength(20);

        // PostgreSQL's system column xmin changes on every row update: a free optimistic-concurrency token.
        builder.Property(t => t.Version).IsRowVersion();

        builder.HasIndex(t => new { t.TenantId, t.Number }).IsUnique();
        // Queue views: by status, by assignee, by customer; newest activity first.
        builder.HasIndex(t => new { t.TenantId, t.Status, t.UpdatedAt });
        builder.HasIndex(t => new { t.TenantId, t.AssigneeUserId, t.Status });
        builder.HasIndex(t => new { t.TenantId, t.CustomerId });

        builder.HasOne<Tenant>().WithMany().HasForeignKey(t => t.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Customer>().WithMany().HasForeignKey(t => t.CustomerId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<User>().WithMany().HasForeignKey(t => t.AssigneeUserId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<User>().WithMany().HasForeignKey(t => t.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.Property(a => a.Action).HasMaxLength(100).IsRequired();
        builder.Property(a => a.EntityType).HasMaxLength(100).IsRequired();
        builder.Property(a => a.ActorType).HasConversion<string>().HasMaxLength(20);
        builder.Property(a => a.CorrelationId).HasMaxLength(64);
        builder.OwnsMany(a => a.Changes, c => c.ToJson());

        // Entity history, and the admin log (newest first, optionally by actor type).
        builder.HasIndex(a => new { a.TenantId, a.EntityType, a.EntityId, a.CreatedAt });
        builder.HasIndex(a => new { a.TenantId, a.CreatedAt });

        // No FK to users: audit entries must survive user deletion unchanged.
        builder.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>One row per tenant holding the last issued ticket number.</summary>
internal sealed class TicketCounter
{
    public Guid TenantId { get; set; }

    public int LastNumber { get; set; }
}

internal sealed class TicketCounterConfiguration : IEntityTypeConfiguration<TicketCounter>
{
    public void Configure(EntityTypeBuilder<TicketCounter> builder)
    {
        builder.HasKey(c => c.TenantId);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}
