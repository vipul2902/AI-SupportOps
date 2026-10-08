using AISupportOps.Application.Auditing;
using AISupportOps.Application.Common;
using AISupportOps.Application.Documents;
using AISupportOps.Domain.Tenants;
using AISupportOps.Domain.Tickets;
using Microsoft.EntityFrameworkCore;

namespace AISupportOps.Application.Tickets;

/// <summary>
/// Ticket use cases. Used by the REST API now and by the AI agent's ticket tools in Phase 10,
/// so every rule (validation, workflow, assignment, audit) applies identically to both.
/// </summary>
public sealed class TicketService(
    IApplicationDbContext db,
    ICurrentUser currentUser,
    ITicketNumberGenerator numbers,
    AuditTrail audit,
    TimeProvider time)
{
    public const string EntityType = "SupportTicket";

    public async Task<TicketResponse> CreateAsync(CreateTicketRequest request, TicketSource source, CancellationToken ct)
    {
        var tenantId = currentUser.RequireTenantId();
        var userId = currentUser.RequireUserId();
        if (request.CustomerId is { } customerId)
        {
            await EnsureCustomerExistsAsync(customerId, ct);
        }

        if (request.AssigneeUserId is { } assigneeId)
        {
            await EnsureAssignableAsync(assigneeId, ct);
        }

        var number = await numbers.NextAsync(tenantId, ct);
        var ticket = new SupportTicket(tenantId, number, request.Title, request.Description ?? string.Empty, request.Priority, request.CustomerId, userId, source);
        ticket.Assign(request.AssigneeUserId);
        db.SupportTickets.Add(ticket);

        audit.Record("ticket.created", EntityType, ticket.Id, new ChangeSet()
            .Track("number", (int?)null, ticket.Number)
            .Track("title", null, ticket.Title)
            .Track("status", (TicketStatus?)null, ticket.Status)
            .Track("priority", (TicketPriority?)null, ticket.Priority)
            .Track("customerId", (Guid?)null, ticket.CustomerId)
            .Track("assigneeUserId", (Guid?)null, ticket.AssigneeUserId)
            .Track("source", (TicketSource?)null, ticket.Source)
            .Changes);

        await db.SaveChangesAsync(ct);
        return await GetAsync(ticket.Id, ct);
    }

    public async Task<TicketResponse> GetAsync(Guid id, CancellationToken ct) =>
        await Project(db.SupportTickets.Where(t => t.Id == id)).SingleOrDefaultAsync(ct)
            ?? throw new NotFoundException("Ticket not found.");

    public async Task<TicketResponse> GetByNumberAsync(int number, CancellationToken ct) =>
        await Project(db.SupportTickets.Where(t => t.Number == number)).SingleOrDefaultAsync(ct)
            ?? throw new NotFoundException($"Ticket #{number} not found.");

    public async Task<PagedResponse<TicketResponse>> ListAsync(TicketQuery query, CancellationToken ct)
    {
        var page = Math.Max(query.Page, 1);
        var pageSize = Math.Clamp(query.PageSize, 1, DocumentService.MaxPageSize);
        var tickets = db.SupportTickets.AsNoTracking();

        if (query.Status is { } status)
        {
            tickets = tickets.Where(t => t.Status == status);
        }

        if (query.Priority is { } priority)
        {
            tickets = tickets.Where(t => t.Priority == priority);
        }

        if (query.CustomerId is { } customerId)
        {
            tickets = tickets.Where(t => t.CustomerId == customerId);
        }

        tickets = query.Assignee?.Trim().ToUpperInvariant() switch
        {
            null or "" => tickets,
            "ME" => tickets.Where(t => t.AssigneeUserId == currentUser.RequireUserId()),
            "UNASSIGNED" => tickets.Where(t => t.AssigneeUserId == null),
            var value when Guid.TryParse(value, out var assigneeId) => tickets.Where(t => t.AssigneeUserId == assigneeId),
            _ => throw new BusinessRuleException("Assignee filter must be 'me', 'unassigned', or a user id."),
        };

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            var upper = term.ToUpperInvariant();
            var number = int.TryParse(term.TrimStart('#'), out var n) ? n : -1;
#pragma warning disable CA1862, CA1304, CA1311 // Translated to SQL upper(...) LIKE; never runs in .NET, so culture rules do not apply.
            tickets = tickets.Where(t => t.Number == number || t.Title.ToUpper().Contains(upper));
#pragma warning restore CA1862, CA1304, CA1311
        }

        var total = await tickets.CountAsync(ct);
        var items = await Project(tickets.OrderByDescending(t => t.UpdatedAt).Skip((page - 1) * pageSize).Take(pageSize)).ToListAsync(ct);
        return new PagedResponse<TicketResponse>(items, page, pageSize, total);
    }

    public async Task<TicketResponse> UpdateAsync(Guid id, UpdateTicketRequest request, CancellationToken ct)
    {
        var ticket = await db.SupportTickets.SingleOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new NotFoundException("Ticket not found.");

        if (request.ExpectedVersion is { } expected)
        {
            if (expected != ticket.Version)
            {
                throw new ConflictException("The ticket was changed by someone else. Reload and try again.");
            }

            // Also enforce at the database: the UPDATE matches only if xmin still equals the expected version.
            db.Entry(ticket).Property(t => t.Version).OriginalValue = expected;
        }

        var changes = new ChangeSet();
        if (request.Title is { } title)
        {
            changes.Track("title", ticket.Title, title.Trim());
            ticket.SetTitle(title);
        }

        if (request.Description is { } description)
        {
            changes.Track("description", ticket.Description, description.Trim());
            ticket.SetDescription(description);
        }

        if (request.Priority is { } priority)
        {
            changes.Track("priority", ticket.Priority, priority);
            ticket.SetPriority(priority);
        }

        if (request.Status is { } status)
        {
            if (!SupportTicket.CanTransition(ticket.Status, status))
            {
                throw new BusinessRuleException(
                    $"A ticket cannot move from {ticket.Status} to {status}. Allowed: {string.Join(", ", SupportTicket.NextStatuses(ticket.Status))}.");
            }

            changes.Track("status", ticket.Status, status);
            ticket.ChangeStatus(status, time.GetUtcNow());
        }

        if (request.Unassign || request.AssigneeUserId is not null)
        {
            var newAssignee = request.Unassign ? null : request.AssigneeUserId;
            if (newAssignee is { } assigneeId)
            {
                await EnsureAssignableAsync(assigneeId, ct);
            }

            changes.Track("assigneeUserId", ticket.AssigneeUserId, newAssignee);
            ticket.Assign(newAssignee);
        }

        if (request.UnlinkCustomer || request.CustomerId is not null)
        {
            var newCustomer = request.UnlinkCustomer ? null : request.CustomerId;
            if (newCustomer is { } customerId)
            {
                await EnsureCustomerExistsAsync(customerId, ct);
            }

            changes.Track("customerId", ticket.CustomerId, newCustomer);
            ticket.SetCustomer(newCustomer);
        }

        if (changes.IsEmpty)
        {
            return await GetAsync(id, ct);
        }

        audit.Record("ticket.updated", EntityType, ticket.Id, changes.Changes);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("The ticket was changed by someone else. Reload and try again.");
        }

        return await GetAsync(id, ct);
    }

    public async Task<IReadOnlyList<AuditEntryResponse>> HistoryAsync(Guid id, CancellationToken ct)
    {
        if (!await db.SupportTickets.AnyAsync(t => t.Id == id, ct))
        {
            throw new NotFoundException("Ticket not found.");
        }

        // Order the entity query, then project: EF cannot order by a DTO constructed in the projection.
        return await AuditQueries.Project(db, db.AuditLogs
                .Where(a => a.EntityType == EntityType && a.EntityId == id)
                .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id))
            .ToListAsync(ct);
    }

    /// <summary>Only members of this tenant who can work tickets (Agent or above) may be assigned.</summary>
    private async Task EnsureAssignableAsync(Guid userId, CancellationToken ct)
    {
        var role = await db.TenantMemberships.Where(m => m.UserId == userId).Select(m => (TenantRole?)m.Role).SingleOrDefaultAsync(ct);
        if (role is null or < TenantRole.Agent)
        {
            throw new BusinessRuleException("Tickets can only be assigned to members with the Agent role or higher.");
        }
    }

    private async Task EnsureCustomerExistsAsync(Guid customerId, CancellationToken ct)
    {
        if (!await db.Customers.AnyAsync(c => c.Id == customerId, ct))
        {
            throw new BusinessRuleException("Customer not found.");
        }
    }

    private IQueryable<TicketResponse> Project(IQueryable<SupportTicket> tickets) =>
        from t in tickets
        join c in db.Customers on t.CustomerId equals (Guid?)c.Id into customers
        from c in customers.DefaultIfEmpty()
        join u in db.Users on t.AssigneeUserId equals (Guid?)u.Id into users
        from u in users.DefaultIfEmpty()
        select new TicketResponse(
            t.Id, t.Number, t.Title, t.Description, t.Status, t.Priority, t.Source,
            c == null ? null : new CustomerRef(c.Id, c.Name, c.Email),
            u == null ? null : new UserRef(u.Id, u.DisplayName),
            t.CreatedByUserId, t.CreatedAt, t.UpdatedAt, t.ResolvedAt, t.ClosedAt, t.Version,
            SupportTicket.NextStatuses(t.Status));
}
