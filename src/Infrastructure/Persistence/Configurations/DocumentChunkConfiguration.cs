using AISupportOps.Domain.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pgvector;

namespace AISupportOps.Infrastructure.Persistence.Configurations;

internal sealed class DocumentChunkConfiguration : IEntityTypeConfiguration<DocumentChunk>
{
    public void Configure(EntityTypeBuilder<DocumentChunk> builder)
    {
        builder.Property(c => c.Content).IsRequired();
        builder.Property(c => c.Heading).HasMaxLength(DocumentChunk.HeadingMaxLength);
        builder.Property(c => c.EmbeddingModel).HasMaxLength(100);

        // Domain keeps a plain float[]; the database stores a pgvector column.
        builder.Property(c => c.Embedding)
            .HasColumnType($"vector({DocumentChunk.EmbeddingDimensions})")
            .HasConversion(v => new Vector(v), v => v.ToArray(), new ValueComparer<float[]>(
                (a, b) => ReferenceEquals(a, b) || (a != null && b != null && a.SequenceEqual(b)),
                v => v.Length,
                v => v));

        // HNSW approximate nearest-neighbour index for cosine distance (<=>).
        // m / ef_construction left at pgvector defaults (16 / 64): good recall at this scale.
        builder.HasIndex(c => c.Embedding)
            .HasMethod("hnsw")
            .HasOperators("vector_cosine_ops");

        // Reading order within a document; leading TenantId serves tenant-filtered queries.
        builder.HasIndex(c => new { c.TenantId, c.DocumentId, c.Index }).IsUnique();
        builder.HasIndex(c => c.DocumentId);

        // Deleting a document deletes its chunks (and, from Phase 5, their embeddings).
        builder.HasOne<Document>().WithMany().HasForeignKey(c => c.DocumentId).OnDelete(DeleteBehavior.Cascade);
    }
}
