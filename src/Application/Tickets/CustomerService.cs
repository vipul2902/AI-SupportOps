using AISupportOps.Application.Auditing;
using AISupportOps.Application.Common;
using AISupportOps.Application.Documents;
using AISupportOps.Domain.Identity;
using AISupportOps.Domain.Tickets;
using Microsoft.EntityFrameworkCore;

namespace AISupportOps.Application.Tickets;

public sealed class CustomerService(IApplicationDbContext db, ICurrentUser currentUser, AuditTrail audit)
{
    public const string EntityType = "Customer";

    public async Task<CustomerResponse> CreateAsync(CreateCustomerRequest request, CancellationToken ct)
    {
        var normalized = User.NormalizeEmail(request.Email);
        if (await db.Customers.AnyAsync(c => c.NormalizedEmail == normalized, ct))
        {
            throw new ConflictException("A customer with this email already exists.");
        }

        var customer = new Customer(currentUser.RequireTenantId(), request.Name, request.Email, request.Company);
        db.Customers.Add(customer);
        audit.Record("customer.created", EntityType, customer.Id, new ChangeSet()
            .Track("name", null, customer.Name)
            .Track("email", null, customer.Email)
            .Changes);
        await db.SaveChangesAsync(ct);

        return ToResponse(customer);
    }

    public async Task<PagedResponse<CustomerResponse>> ListAsync(string? search, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, DocumentService.MaxPageSize);
        var customers = db.Customers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var upper = search.Trim().ToUpperInvariant();
#pragma warning disable CA1862, CA1304, CA1311 // Translated to SQL upper(...) LIKE; never runs in .NET, so culture rules do not apply.
            customers = customers.Where(c => c.Name.ToUpper().Contains(upper) || c.NormalizedEmail.Contains(upper));
#pragma warning restore CA1862, CA1304, CA1311
        }

        var total = await customers.CountAsync(ct);
        var items = await customers.OrderBy(c => c.Name).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(c => new CustomerResponse(c.Id, c.Name, c.Email, c.Company, c.CreatedAt))
            .ToListAsync(ct);
        return new PagedResponse<CustomerResponse>(items, page, pageSize, total);
    }

    public async Task<CustomerDetail> GetAsync(Guid id, CancellationToken ct) =>
        await DetailAsync(db.Customers.Where(c => c.Id == id), ct) ?? throw new NotFoundException("Customer not found.");

    public async Task<CustomerDetail> GetByEmailAsync(string email, CancellationToken ct)
    {
        var normalized = User.NormalizeEmail(email);
        return await DetailAsync(db.Customers.Where(c => c.NormalizedEmail == normalized), ct)
            ?? throw new NotFoundException("Customer not found.");
    }

    private async Task<CustomerDetail?> DetailAsync(IQueryable<Customer> query, CancellationToken ct)
    {
        var customer = await query.AsNoTracking().SingleOrDefaultAsync(ct);
        if (customer is null)
        {
            return null;
        }

        var tickets = db.SupportTickets.AsNoTracking().Where(t => t.CustomerId == customer.Id);
        var open = await tickets.CountAsync(t => t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed, ct);
        var recent = await tickets.OrderByDescending(t => t.UpdatedAt).Take(5)
            .Select(t => new TicketSummary(t.Id, t.Number, t.Title, t.Status, t.Priority, t.UpdatedAt))
            .ToListAsync(ct);

        return new CustomerDetail(ToResponse(customer), open, recent);
    }

    private static CustomerResponse ToResponse(Customer c) => new(c.Id, c.Name, c.Email, c.Company, c.CreatedAt);
}
