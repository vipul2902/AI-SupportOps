using AISupportOps.Domain.Documents;
using AISupportOps.Domain.Identity;
using AISupportOps.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AISupportOps.Infrastructure.Persistence.Configurations;

internal sealed class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    public void Configure(EntityTypeBuilder<Document> builder)
    {
        builder.Property(d => d.FileName).HasMaxLength(Document.FileNameMaxLength).IsRequired();
        builder.Property(d => d.ContentType).HasMaxLength(100).IsRequired();
        builder.Property(d => d.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(d => d.Sha256).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.Property(d => d.StorageKey).HasMaxLength(200).IsRequired();
        builder.Property(d => d.Error).HasMaxLength(Document.ErrorMaxLength);

        // Same file can't be uploaded twice to one tenant; different tenants are independent.
        builder.HasIndex(d => new { d.TenantId, d.Sha256 }).IsUnique();
        // Serves the default listing (newest first) and status filtering within a tenant.
        builder.HasIndex(d => new { d.TenantId, d.CreatedAt });
        builder.HasIndex(d => new { d.TenantId, d.Status });
        builder.HasIndex(d => d.StorageKey).IsUnique();

        builder.HasOne<Tenant>().WithMany().HasForeignKey(d => d.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(d => d.UploadedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
