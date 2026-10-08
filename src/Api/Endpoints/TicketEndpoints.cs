using AISupportOps.Api.Auth;
using AISupportOps.Application.Auditing;
using AISupportOps.Application.Tickets;
using AISupportOps.Domain.Auditing;
using AISupportOps.Domain.Tickets;
using Microsoft.AspNetCore.Mvc;

namespace AISupportOps.Api.Endpoints;

internal static class TicketEndpoints
{
    public static IEndpointRouteBuilder MapTicketEndpoints(this IEndpointRouteBuilder app)
    {
        var tickets = app.MapGroup("/api/tickets").WithTags("Tickets");

        tickets.MapGet("/", ([AsParameters] TicketQuery query, TicketService svc, CancellationToken ct) => svc.ListAsync(query, ct))
            .RequireAuthorization(Policies.Viewer);

        tickets.MapPost("/", async (CreateTicketRequest request, TicketService svc, CancellationToken ct) =>
            {
                var created = await svc.CreateAsync(request, TicketSource.Manual, ct);
                return Results.Created($"/api/tickets/{created.Id}", created);
            })
            .RequireAuthorization(Policies.Agent);

        tickets.MapGet("/{id:guid}", (Guid id, TicketService svc, CancellationToken ct) => svc.GetAsync(id, ct))
            .RequireAuthorization(Policies.Viewer);

        tickets.MapGet("/number/{number:int}", (int number, TicketService svc, CancellationToken ct) => svc.GetByNumberAsync(number, ct))
            .RequireAuthorization(Policies.Viewer);

        tickets.MapPatch("/{id:guid}", (Guid id, UpdateTicketRequest request, TicketService svc, CancellationToken ct) =>
                svc.UpdateAsync(id, request, ct))
            .RequireAuthorization(Policies.Agent);

        tickets.MapGet("/{id:guid}/history", (Guid id, TicketService svc, CancellationToken ct) => svc.HistoryAsync(id, ct))
            .RequireAuthorization(Policies.Viewer);

        var customers = app.MapGroup("/api/customers").WithTags("Customers");

        customers.MapGet("/", (CustomerService svc, CancellationToken ct, string? search, int page = 1, int pageSize = 20) =>
                svc.ListAsync(search, page, pageSize, ct))
            .RequireAuthorization(Policies.Viewer);

        customers.MapPost("/", async (CreateCustomerRequest request, CustomerService svc, CancellationToken ct) =>
            {
                var created = await svc.CreateAsync(request, ct);
                return Results.Created($"/api/customers/{created.Id}", created);
            })
            .RequireAuthorization(Policies.Agent);

        customers.MapGet("/{id:guid}", (Guid id, CustomerService svc, CancellationToken ct) => svc.GetAsync(id, ct))
            .RequireAuthorization(Policies.Viewer);

        app.MapGet("/api/audit-logs", (AuditLogService svc, CancellationToken ct, string? entityType, Guid? entityId,
                    AuditActorType? actorType, int page = 1, int pageSize = 50) =>
                svc.ListAsync(entityType, entityId, actorType, page, pageSize, ct))
            .WithTags("Audit")
            .RequireAuthorization(Policies.Admin);

        return app;
    }
}
