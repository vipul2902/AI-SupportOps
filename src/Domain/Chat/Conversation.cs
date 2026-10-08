using AISupportOps.Domain.Common;

namespace AISupportOps.Domain.Chat;

/// <summary>A chat thread. Tenant-owned and private to the user who started it.</summary>
public sealed class Conversation : Entity, ITenantOwned
{
    public const int TitleMaxLength = 120;

    private Conversation()
    {
    }

    public Conversation(Guid tenantId, Guid userId, string firstMessage, DateTimeOffset now)
    {
        TenantId = tenantId;
        UserId = userId;
        Title = TitleFrom(firstMessage);
        LastMessageAt = now;
    }

    public Guid TenantId { get; private set; }

    public Guid UserId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public DateTimeOffset LastMessageAt { get; private set; }

    /// <summary>Running summary of messages that have left the short-term window (long-range memory).</summary>
    public string? Summary { get; private set; }

    /// <summary>CreatedAt of the newest message folded into <see cref="Summary"/>.</summary>
    public DateTimeOffset? SummarizedUntil { get; private set; }

    public void UpdateSummary(string summary, DateTimeOffset summarizedUntil)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        if (SummarizedUntil is { } previous && summarizedUntil <= previous)
        {
            throw new InvalidOperationException("Summary can only move forward in time.");
        }

        Summary = summary;
        SummarizedUntil = summarizedUntil;
    }

    public void Rename(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        Title = TitleFrom(title);
    }

    public void Touch(DateTimeOffset now) => LastMessageAt = now;

    /// <summary>Derived from the first message, no LLM call: free, instant, and good enough.</summary>
    private static string TitleFrom(string text)
    {
        var singleLine = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return singleLine.Length <= TitleMaxLength ? singleLine : singleLine[..(TitleMaxLength - 1)].TrimEnd() + "…";
    }
}
