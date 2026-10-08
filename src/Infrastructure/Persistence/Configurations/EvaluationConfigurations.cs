using AISupportOps.Domain.Evaluation;
using AISupportOps.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AISupportOps.Infrastructure.Persistence.Configurations;

internal sealed class EvaluationRunConfiguration : IEntityTypeConfiguration<EvaluationRun>
{
    public void Configure(EntityTypeBuilder<EvaluationRun> builder)
    {
        builder.Property(r => r.Name).HasMaxLength(100).IsRequired();
        builder.Property(r => r.PromptId).HasMaxLength(100).IsRequired();
        builder.Property(r => r.ChatModel).HasMaxLength(100).IsRequired();
        builder.Property(r => r.EmbeddingModel).HasMaxLength(100).IsRequired();

        // Aggregate scores as one jsonb document: read together, never queried individually.
        builder.OwnsOne(r => r.Summary, s => s.ToJson());

        builder.HasIndex(r => new { r.TenantId, r.CreatedAt });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class EvaluationResultConfiguration : IEntityTypeConfiguration<EvaluationResult>
{
    public void Configure(EntityTypeBuilder<EvaluationResult> builder)
    {
        builder.Property(r => r.CaseId).HasMaxLength(100).IsRequired();
        builder.Property(r => r.Question).IsRequired();
        builder.Property(r => r.ExpectedSource).HasMaxLength(255);
        builder.Property(r => r.Outcome).HasMaxLength(40).IsRequired();
        builder.Property(r => r.JudgeNotes).HasMaxLength(2000);

        builder.HasIndex(r => new { r.TenantId, r.RunId });
        builder.HasOne<EvaluationRun>().WithMany().HasForeignKey(r => r.RunId).OnDelete(DeleteBehavior.Cascade);
    }
}
