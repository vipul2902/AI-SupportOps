using AISupportOps.Domain.Common;

namespace AISupportOps.Domain.Auditing;

public enum AuditActorType
{
    User,

    /// <summary>The AI agent acting on behalf of a user (Phase 10). The user is still recorded.</summary>
    AiAgent,

    System,
}

/// <summary>Old and new value of one field, as strings for display and diffing.</summary>
public sealed class AuditChange
{
    public string Field { get; set; } = string.Empty;

    public string? From { get; set; }

    public string? To { get; set; }
}

/// <summary>
/// Append-only record of a state change: who (user, and whether via the AI agent), what (action and
/// entity), how (field-level changes), and when. Written in the same transaction as the change itself,
/// so there is never a change without its audit entry, or an entry for a change that rolled back.
/// </summary>
public sealed class AuditLog : Entity, ITenantOwned
{
    private AuditLog()
    {
    }

    public AuditLog(Guid tenantId, Guid? actorUserId, AuditActorType actorType, string action, string entityType, Guid entityId, IEnumerable<AuditChange> changes, string? correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        TenantId = tenantId;
        ActorUserId = actorUserId;
        ActorType = actorType;
        Action = action;
        EntityType = entityType;
        EntityId = entityId;
        Changes = changes.ToList();
        CorrelationId = correlationId;
    }

    public Guid TenantId { get; private set; }

    public Guid? ActorUserId { get; private set; }

    public AuditActorType ActorType { get; private set; }

    /// <summary>Dotted verb, e.g. "ticket.created", "ticket.updated".</summary>
    public string Action { get; private set; } = string.Empty;

    public string EntityType { get; private set; } = string.Empty;

    public Guid EntityId { get; private set; }

    public List<AuditChange> Changes { get; private set; } = [];

    /// <summary>Request trace id, linking the audit entry to logs and traces.</summary>
    public string? CorrelationId { get; private set; }
}
