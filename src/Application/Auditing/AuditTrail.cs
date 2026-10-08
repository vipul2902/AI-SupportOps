using System.Diagnostics;
using System.Globalization;
using AISupportOps.Application.Common;
using AISupportOps.Domain.Auditing;

namespace AISupportOps.Application.Auditing;

/// <summary>
/// Records audit entries into the current unit of work, so they commit (or roll back) atomically
/// with the change they describe. Scoped per request; the AI agent sets <see cref="ActorType"/> to
/// <see cref="AuditActorType.AiAgent"/> so its actions are distinguishable while still attributed
/// to the user it acted for.
/// </summary>
public sealed class AuditTrail(IApplicationDbContext db, ICurrentUser currentUser)
{
    public AuditActorType ActorType { get; set; } = AuditActorType.User;

    public void Record(string action, string entityType, Guid entityId, IReadOnlyList<AuditChange> changes) =>
        db.AuditLogs.Add(new AuditLog(
            currentUser.RequireTenantId(),
            currentUser.UserId,
            ActorType,
            action,
            entityType,
            entityId,
            changes,
            Activity.Current?.TraceId.ToString()));
}

/// <summary>Collects field-level changes, skipping fields whose value did not actually change.</summary>
public sealed class ChangeSet
{
    private readonly List<AuditChange> _changes = [];

    public IReadOnlyList<AuditChange> Changes => _changes;

    public bool IsEmpty => _changes.Count == 0;

    public ChangeSet Track<T>(string field, T from, T to)
    {
        if (!EqualityComparer<T>.Default.Equals(from, to))
        {
            _changes.Add(new AuditChange { Field = field, From = Format(from), To = Format(to) });
        }

        return this;
    }

    private static string? Format<T>(T value) => value switch
    {
        null => null,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}
