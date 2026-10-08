using AISupportOps.Application.Common;
using AISupportOps.Application.Documents;
using AISupportOps.Application.Tickets;
using AISupportOps.Domain.Auditing;
using Microsoft.EntityFrameworkCore;

namespace AISupportOps.Application.Auditing;

public static class AuditQueries
{
    /// <summary>Audit reads are always non-tracking: entries are append-only, and owned JSON changes can't be tracked without their owner.</summary>
    public static IQueryable<AuditEntryResponse> Project(IApplicationDbContext db, IQueryable<AuditLog> logs) =>
        from a in logs.AsNoTracking()
        join u in db.Users on a.ActorUserId equals (Guid?)u.Id into users
        from u in users.DefaultIfEmpty()
        select new AuditEntryResponse(a.Id, a.Action, a.EntityType, a.EntityId, a.ActorType, a.ActorUserId,
            u == null ? null : u.DisplayName, a.Changes, a.CorrelationId, a.CreatedAt);
}

/// <summary>Organization-wide audit log for administrators.</summary>
public sealed class AuditLogService(IApplicationDbContext db)
{
    public async Task<PagedResponse<AuditEntryResponse>> ListAsync(
        string? entityType, Guid? entityId, AuditActorType? actorType, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, DocumentService.MaxPageSize);
        var logs = db.AuditLogs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(entityType))
        {
            logs = logs.Where(a => a.EntityType == entityType);
        }

        if (entityId is { } id)
        {
            logs = logs.Where(a => a.EntityId == id);
        }

        if (actorType is { } actor)
        {
            logs = logs.Where(a => a.ActorType == actor);
        }

        var total = await logs.CountAsync(ct);
        var items = await AuditQueries.Project(db, logs.OrderByDescending(a => a.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize))
            .ToListAsync(ct);
        return new PagedResponse<AuditEntryResponse>(items, page, pageSize, total);
    }
}
