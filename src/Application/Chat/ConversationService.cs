using AISupportOps.Application.Common;
using AISupportOps.Application.Documents;
using AISupportOps.Application.Evaluation;
using AISupportOps.Domain.Chat;
using Microsoft.EntityFrameworkCore;

namespace AISupportOps.Application.Chat;

/// <summary>Reading and managing the current user's own conversations.</summary>
public sealed class ConversationService(IApplicationDbContext db, ICurrentUser currentUser, TimeProvider time)
{
    public async Task<PagedResponse<ConversationSummary>> ListAsync(int page, int pageSize, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, DocumentService.MaxPageSize);

        var query = db.Conversations.AsNoTracking().Where(c => c.UserId == userId);
        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(c => c.LastMessageAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new ConversationSummary(c.Id, c.Title, c.CreatedAt, c.LastMessageAt))
            .ToListAsync(ct);

        return new PagedResponse<ConversationSummary>(items, page, pageSize, total);
    }

    public async Task<ConversationDetail> GetAsync(Guid id, CancellationToken ct)
    {
        var conversation = await FindAsync(id, ct);
        var messages = await db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == id)
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .Select(m => new MessageResponse(m.Id, m.Role, m.Content, m.Status, m.Outcome, m.Citations, m.CreatedAt))
            .ToListAsync(ct);

        return new ConversationDetail(conversation.Id, conversation.Title, conversation.CreatedAt, conversation.Summary, messages);
    }

    public async Task<ConversationSummary> RenameAsync(Guid id, RenameConversationRequest request, CancellationToken ct)
    {
        var conversation = await FindAsync(id, ct);
        conversation.Rename(request.Title);
        await db.SaveChangesAsync(ct);
        return new ConversationSummary(conversation.Id, conversation.Title, conversation.CreatedAt, conversation.LastMessageAt);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var conversation = await FindAsync(id, ct);
        db.Conversations.Remove(conversation); // messages cascade
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Thumbs up/down on an assistant answer in one of the caller's own conversations.</summary>
    public async Task SetFeedbackAsync(Guid conversationId, Guid messageId, FeedbackRequest request, CancellationToken ct)
    {
        await FindAsync(conversationId, ct);
        var message = await db.Messages.SingleOrDefaultAsync(m => m.Id == messageId && m.ConversationId == conversationId, ct)
            ?? throw new NotFoundException("Message not found.");
        if (message.Role != MessageRole.Assistant)
        {
            throw new BusinessRuleException("Feedback can only be given on assistant answers.");
        }

        message.SetFeedback(request.Helpful, request.Comment, time.GetUtcNow());
        await db.SaveChangesAsync(ct);
    }

    private async Task<Conversation> FindAsync(Guid id, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        return await db.Conversations.SingleOrDefaultAsync(c => c.Id == id && c.UserId == userId, ct)
            ?? throw new NotFoundException("Conversation not found.");
    }
}
