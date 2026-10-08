using AISupportOps.Domain.Chat;
using AISupportOps.Domain.Identity;
using AISupportOps.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AISupportOps.Infrastructure.Persistence.Configurations;

internal sealed class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> builder)
    {
        builder.Property(c => c.Title).HasMaxLength(Conversation.TitleMaxLength).IsRequired();
        builder.Property(c => c.Summary).HasMaxLength(2000);

        // Sidebar query: my conversations, most recent first.
        builder.HasIndex(c => new { c.TenantId, c.UserId, c.LastMessageAt });

        builder.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.Property(m => m.Content).IsRequired();
        builder.Property(m => m.Role).HasConversion<string>().HasMaxLength(20);
        builder.Property(m => m.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(m => m.Outcome).HasMaxLength(40);
        builder.Property(m => m.RetrievalQuery).HasMaxLength(4000);
        builder.Property(m => m.Model).HasMaxLength(100);
        builder.Property(m => m.PromptId).HasMaxLength(100);

        // Citations are a point-in-time snapshot of what the answer cited: one jsonb column, no join table.
        builder.OwnsMany(m => m.Citations, c => c.ToJson());

        // Conversation transcript in order.
        builder.HasIndex(m => new { m.TenantId, m.ConversationId, m.CreatedAt });

        builder.HasOne<Conversation>().WithMany().HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.Cascade);
    }
}
