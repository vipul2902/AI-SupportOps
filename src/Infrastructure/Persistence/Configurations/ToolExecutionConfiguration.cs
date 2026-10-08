using AISupportOps.Domain.Agents;
using AISupportOps.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AISupportOps.Infrastructure.Persistence.Configurations;

internal sealed class ToolExecutionConfiguration : IEntityTypeConfiguration<ToolExecution>
{
    public void Configure(EntityTypeBuilder<ToolExecution> builder)
    {
        builder.Property(t => t.ToolName).HasMaxLength(100).IsRequired();
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(t => t.ArgumentsJson).IsRequired();

        // Per-run timeline, per-tool analytics, and "show me denied calls" security review.
        builder.HasIndex(t => new { t.TenantId, t.RunId });
        builder.HasIndex(t => new { t.TenantId, t.ToolName, t.CreatedAt });
        builder.HasIndex(t => new { t.TenantId, t.Status, t.CreatedAt });

        builder.HasOne<Tenant>().WithMany().HasForeignKey(t => t.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}
