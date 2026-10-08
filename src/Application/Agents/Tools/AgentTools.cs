using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using AISupportOps.Application.Knowledge;
using AISupportOps.Application.Tickets;
using AISupportOps.Domain.Identity;
using AISupportOps.Domain.Tenants;
using AISupportOps.Domain.Tickets;
using AISupportOps.Application.Common;
using Microsoft.EntityFrameworkCore;

namespace AISupportOps.Application.Agents.Tools;

// Argument records use [property: ...] so DataAnnotations validation sees the attributes.

public sealed record SearchKnowledgeBaseArgs(
    [property: Required, StringLength(500, MinimumLength = 2), Description("What to look up, phrased as a question or keywords.")] string Query,
    [property: Range(1, 8), Description("How many passages to return (default 5).")] int? TopK = null);

public sealed class SearchKnowledgeBaseTool(KnowledgeSearchService search) : AgentTool<SearchKnowledgeBaseArgs>
{
    public override string Name => "search_knowledge_base";

    public override string Description =>
        "Search the organization's documentation. Use it to answer how-to and policy questions. Returns passages with source file, page and section.";

    public override TenantRole MinimumRole => TenantRole.Viewer;

    public override bool IsWrite => false;

    protected override async Task<ToolResult> ExecuteAsync(SearchKnowledgeBaseArgs args, CancellationToken ct)
    {
        var response = await search.SearchAsync(new SearchRequest(args.Query, args.TopK ?? 5, MinScore: 0.2), ct);
        return ToolResult.Ok(new
        {
            results = response.Results.Select(r => new
            {
                source = r.FileName,
                page = r.PageNumber,
                section = r.Heading,
                score = r.Score,
                content = r.Content.Length > 1200 ? r.Content[..1200] + "…" : r.Content,
            }),
        });
    }
}

public sealed record GetSupportTicketArgs(
    [property: Range(1, int.MaxValue), Description("The ticket number, e.g. 42 for ticket #42.")] int TicketNumber);

public sealed class GetSupportTicketTool(TicketService tickets) : AgentTool<GetSupportTicketArgs>
{
    public override string Name => "get_support_ticket";

    public override string Description => "Get the details and current status of a support ticket by its number.";

    public override TenantRole MinimumRole => TenantRole.Viewer;

    public override bool IsWrite => false;

    protected override async Task<ToolResult> ExecuteAsync(GetSupportTicketArgs args, CancellationToken ct) =>
        ToolResult.Ok(TicketView.From(await tickets.GetByNumberAsync(args.TicketNumber, ct)));
}

public sealed record CreateSupportTicketArgs(
    [property: Required, StringLength(SupportTicket.TitleMaxLength, MinimumLength = 3), Description("Short summary of the issue.")] string Title,
    [property: Required, StringLength(4000, MinimumLength = 1), Description("Details of the issue as described by the user.")] string Description,
    [property: Description("Urgency. Use critical only for outages or security issues.")] TicketPriority Priority = TicketPriority.Medium,
    [property: EmailAddress, Description("Email of the affected customer, if known.")] string? CustomerEmail = null);

public sealed class CreateSupportTicketTool(TicketService tickets, IApplicationDbContext db) : AgentTool<CreateSupportTicketArgs>
{
    public override string Name => "create_support_ticket";

    public override string Description =>
        "Create a new support ticket. Only use when the user asks for a ticket to be created or clearly reports a problem that needs follow-up.";

    public override TenantRole MinimumRole => TenantRole.Agent;

    public override bool IsWrite => true;

    protected override async Task<ToolResult> ExecuteAsync(CreateSupportTicketArgs args, CancellationToken ct)
    {
        Guid? customerId = null;
        if (args.CustomerEmail is { } email)
        {
            var normalized = User.NormalizeEmail(email);
            customerId = await db.Customers.Where(c => c.NormalizedEmail == normalized).Select(c => (Guid?)c.Id).SingleOrDefaultAsync(ct);
            if (customerId is null)
            {
                return ToolResult.Fail($"No customer with email {email} exists. Ask the user to confirm the email, or create the ticket without a customer.");
            }
        }

        var ticket = await tickets.CreateAsync(new CreateTicketRequest(args.Title, args.Description, args.Priority, customerId), TicketSource.AiAgent, ct);
        return ToolResult.Ok(TicketView.From(ticket));
    }
}

public sealed record UpdateSupportTicketArgs(
    [property: Range(1, int.MaxValue), Description("The ticket number to update.")] int TicketNumber,
    [property: Description("New status. Allowed transitions depend on the current status.")] TicketStatus? Status = null,
    [property: Description("New priority.")] TicketPriority? Priority = null,
    [property: EmailAddress, Description("Email of the team member to assign the ticket to.")] string? AssigneeEmail = null);

public sealed class UpdateSupportTicketTool(TicketService tickets, IApplicationDbContext db) : AgentTool<UpdateSupportTicketArgs>
{
    public override string Name => "update_support_ticket";

    public override string Description =>
        "Change the status, priority, or assignee of an existing ticket. Only use when the user explicitly asks for the change.";

    public override TenantRole MinimumRole => TenantRole.Agent;

    public override bool IsWrite => true;

    protected override async Task<ToolResult> ExecuteAsync(UpdateSupportTicketArgs args, CancellationToken ct)
    {
        if (args.Status is null && args.Priority is null && args.AssigneeEmail is null)
        {
            return ToolResult.Fail("Nothing to update: provide status, priority, or assignee_email.");
        }

        var current = await tickets.GetByNumberAsync(args.TicketNumber, ct);
        Guid? assigneeId = null;
        if (args.AssigneeEmail is { } email)
        {
            var normalized = User.NormalizeEmail(email);
            assigneeId = await db.TenantMemberships.Where(m => m.User.NormalizedEmail == normalized).Select(m => (Guid?)m.UserId).SingleOrDefaultAsync(ct);
            if (assigneeId is null)
            {
                return ToolResult.Fail($"No team member with email {email}.");
            }
        }

        var updated = await tickets.UpdateAsync(current.Id,
            new UpdateTicketRequest(Status: args.Status, Priority: args.Priority, AssigneeUserId: assigneeId, ExpectedVersion: current.Version), ct);
        return ToolResult.Ok(TicketView.From(updated));
    }
}

public sealed record GetCustomerInformationArgs(
    [property: Required, EmailAddress, Description("The customer's email address.")] string Email);

public sealed class GetCustomerInformationTool(CustomerService customers) : AgentTool<GetCustomerInformationArgs>
{
    public override string Name => "get_customer_information";

    public override string Description => "Look up a customer by email: profile, number of open tickets, and recent tickets.";

    public override TenantRole MinimumRole => TenantRole.Viewer;

    public override bool IsWrite => false;

    protected override async Task<ToolResult> ExecuteAsync(GetCustomerInformationArgs args, CancellationToken ct)
    {
        var detail = await customers.GetByEmailAsync(args.Email, ct);
        return ToolResult.Ok(new
        {
            name = detail.Customer.Name,
            email = detail.Customer.Email,
            company = detail.Customer.Company,
            open_tickets = detail.OpenTicketCount,
            recent_tickets = detail.RecentTickets.Select(t => new { number = t.Number, t.Title, status = t.Status.ToString(), priority = t.Priority.ToString() }),
        });
    }
}

/// <summary>Compact ticket shape for the model: what it needs, nothing it doesn't (fewer tokens, less leakage).</summary>
internal static class TicketView
{
    public static object From(TicketResponse t) => new
    {
        number = t.Number,
        title = t.Title,
        status = t.Status.ToString(),
        priority = t.Priority.ToString(),
        customer = t.Customer?.Name,
        assignee = t.Assignee?.DisplayName,
        allowed_next_statuses = t.AllowedNextStatuses.Select(s => s.ToString()),
        updated_at = t.UpdatedAt,
    };
}
