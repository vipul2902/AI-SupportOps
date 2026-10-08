using AISupportOps.Domain.Common;

namespace AISupportOps.Domain.Tickets;

public enum TicketStatus
{
    Open,
    InProgress,
    Resolved,
    Closed,
}

public enum TicketPriority
{
    Low,
    Medium,
    High,
    Critical,
}

/// <summary>Who created a ticket. AI-created tickets are distinguishable for review and evaluation.</summary>
public enum TicketSource
{
    Manual,
    AiAgent,
}

/// <summary>
/// A support ticket. Workflow rules (valid status transitions, resolved/closed timestamps) are
/// enforced here so they hold no matter who changes the ticket: a person, the API, or the AI agent.
/// </summary>
public sealed class SupportTicket : Entity, ITenantOwned
{
    public const int TitleMaxLength = 200;
    public const int DescriptionMaxLength = 10_000;

    private static readonly Dictionary<TicketStatus, TicketStatus[]> AllowedTransitions = new()
    {
        [TicketStatus.Open] = [TicketStatus.InProgress, TicketStatus.Resolved, TicketStatus.Closed],
        [TicketStatus.InProgress] = [TicketStatus.Open, TicketStatus.Resolved],
        [TicketStatus.Resolved] = [TicketStatus.InProgress, TicketStatus.Closed],
        [TicketStatus.Closed] = [TicketStatus.Open],
    };

    private SupportTicket()
    {
    }

    public SupportTicket(Guid tenantId, int number, string title, string description, TicketPriority priority, Guid? customerId, Guid createdByUserId, TicketSource source)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        TenantId = tenantId;
        Number = number;
        SetTitle(title);
        SetDescription(description);
        Priority = priority;
        CustomerId = customerId;
        CreatedByUserId = createdByUserId;
        Source = source;
        Status = TicketStatus.Open;
    }

    public Guid TenantId { get; private set; }

    /// <summary>Human-friendly, per-tenant sequential number (#1042).</summary>
    public int Number { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    public TicketStatus Status { get; private set; }

    public TicketPriority Priority { get; private set; }

    public Guid? CustomerId { get; private set; }

    public Guid? AssigneeUserId { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public TicketSource Source { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    /// <summary>Concurrency token (PostgreSQL xmin). A stale value on update means someone else changed the ticket.</summary>
    public uint Version { get; private set; }

    public static bool CanTransition(TicketStatus from, TicketStatus to) =>
        from == to || AllowedTransitions[from].Contains(to);

    public static IReadOnlyList<TicketStatus> NextStatuses(TicketStatus from) => AllowedTransitions[from];

    public void SetTitle(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        Title = title.Trim();
    }

    public void SetDescription(string description) => Description = description?.Trim() ?? string.Empty;

    public void SetPriority(TicketPriority priority) => Priority = priority;

    public void SetCustomer(Guid? customerId) => CustomerId = customerId;

    public void Assign(Guid? userId) => AssigneeUserId = userId;

    public void ChangeStatus(TicketStatus status, DateTimeOffset now)
    {
        if (!CanTransition(Status, status))
        {
            throw new InvalidTicketTransitionException(Status, status);
        }

        if (status == Status)
        {
            return;
        }

        Status = status;
        switch (status)
        {
            case TicketStatus.Resolved:
                ResolvedAt = now;
                break;
            case TicketStatus.Closed:
                ClosedAt = now;
                ResolvedAt ??= now;
                break;
            default:
                // Reopened: clear completion timestamps.
                ResolvedAt = null;
                ClosedAt = null;
                break;
        }
    }
}

public sealed class InvalidTicketTransitionException(TicketStatus from, TicketStatus to)
    : InvalidOperationException($"A ticket cannot move from {from} to {to}. Allowed: {string.Join(", ", SupportTicket.NextStatuses(from))}.")
{
    public TicketStatus From { get; } = from;

    public TicketStatus To { get; } = to;
}
