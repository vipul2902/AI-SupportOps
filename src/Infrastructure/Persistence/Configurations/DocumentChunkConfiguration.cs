using AISupportOps.Domain.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AISupportOps.Infrastructure.Persistence.Configurations;

internal sealed class DocumentChunkConfiguration : IEntityTypeConfiguration<DocumentChunk>
{
    public void Configure(EntityTypeBuilder<DocumentChunk> builder)
    {
        builder.Property(c => c.Content).IsRequired();
        builder.Property(c => c.Heading).HasMaxLength(DocumentChunk.HeadingMaxLength);

        // Reading order within a document; leading TenantId serves tenant-filtered queries.
        builder.HasIndex(c => new { c.TenantId, c.DocumentId, c.Index }).IsUnique();
        builder.HasIndex(c => c.DocumentId);

        // Deleting a document deletes its chunks (and, from Phase 5, their embeddings).
        builder.HasOne<Document>().WithMany().HasForeignKey(c => c.DocumentId).OnDelete(DeleteBehavior.Cascade);
    }
}
