using AISupportOps.Application.Tickets;
using AISupportOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AISupportOps.Infrastructure.Tickets;

/// <summary>
/// Per-tenant ticket numbers via one atomic upsert. The row lock taken by ON CONFLICT DO UPDATE
/// serializes concurrent callers for the same tenant only, so numbers are unique without a global
/// lock and without "SELECT MAX + 1" races. Like a database sequence, a number consumed by a
/// transaction that later fails leaves a gap; uniqueness, not gaplessness, is the requirement.
/// </summary>
internal sealed class TicketNumberGenerator(AppDbContext db) : ITicketNumberGenerator
{
    // ToListAsync, not SingleAsync: EF must not wrap a write statement in a composed SELECT.
    public async Task<int> NextAsync(Guid tenantId, CancellationToken ct) =>
        (await db.Database.SqlQuery<int>($"""
            INSERT INTO ticket_counters (tenant_id, last_number) VALUES ({tenantId}, 1)
            ON CONFLICT (tenant_id) DO UPDATE SET last_number = ticket_counters.last_number + 1
            RETURNING last_number AS "Value"
            """).ToListAsync(ct)).Single();
}
