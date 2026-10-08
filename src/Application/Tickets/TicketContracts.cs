using System.ComponentModel.DataAnnotations;
using AISupportOps.Domain.Auditing;
using AISupportOps.Domain.Identity;
using AISupportOps.Domain.Tickets;

namespace AISupportOps.Application.Tickets;

public sealed record CreateTicketRequest(
    [Required, StringLength(SupportTicket.TitleMaxLength, MinimumLength = 1)] string Title,
    [StringLength(SupportTicket.DescriptionMaxLength)] string? Description = null,
    [EnumDataType(typeof(TicketPriority))] TicketPriority Priority = TicketPriority.Medium,
    Guid? CustomerId = null,
    Guid? AssigneeUserId = null);

/// <summary>
/// PATCH semantics: null means "leave unchanged". Because null cannot also mean "remove", unassigning
/// and unlinking are explicit flags. <see cref="ExpectedVersion"/> enables optimistic concurrency.
/// </summary>
public sealed record UpdateTicketRequest(
    [StringLength(SupportTicket.TitleMaxLength, MinimumLength = 1)] string? Title = null,
    [StringLength(SupportTicket.DescriptionMaxLength)] string? Description = null,
    [EnumDataType(typeof(TicketStatus))] TicketStatus? Status = null,
    [EnumDataType(typeof(TicketPriority))] TicketPriority? Priority = null,
    Guid? AssigneeUserId = null,
    bool Unassign = false,
    Guid? CustomerId = null,
    bool UnlinkCustomer = false,
    uint? ExpectedVersion = null);

public sealed record TicketQuery(
    TicketStatus? Status = null,
    TicketPriority? Priority = null,
    string? Assignee = null, // "me", "unassigned", or a user id
    Guid? CustomerId = null,
    string? Search = null,
    int Page = 1,
    int PageSize = 20);

public sealed record CustomerRef(Guid Id, string Name, string Email);

public sealed record UserRef(Guid Id, string DisplayName);

public sealed record TicketResponse(
    Guid Id,
    int Number,
    string Title,
    string Description,
    TicketStatus Status,
    TicketPriority Priority,
    TicketSource Source,
    CustomerRef? Customer,
    UserRef? Assignee,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ResolvedAt,
    DateTimeOffset? ClosedAt,
    uint Version,
    IReadOnlyList<TicketStatus> AllowedNextStatuses);

public sealed record TicketSummary(Guid Id, int Number, string Title, TicketStatus Status, TicketPriority Priority, DateTimeOffset UpdatedAt);

public sealed record CreateCustomerRequest(
    [Required, StringLength(Customer.NameMaxLength, MinimumLength = 1)] string Name,
    [Required, EmailAddress, MaxLength(User.EmailMaxLength)] string Email,
    [MaxLength(Customer.NameMaxLength)] string? Company = null);

public sealed record CustomerResponse(Guid Id, string Name, string Email, string? Company, DateTimeOffset CreatedAt);

public sealed record CustomerDetail(CustomerResponse Customer, int OpenTicketCount, IReadOnlyList<TicketSummary> RecentTickets);

public sealed record AuditEntryResponse(
    Guid Id,
    string Action,
    string EntityType,
    Guid EntityId,
    AuditActorType ActorType,
    Guid? ActorUserId,
    string? ActorName,
    IReadOnlyList<AuditChange> Changes,
    string? CorrelationId,
    DateTimeOffset CreatedAt);

/// <summary>Allocates per-tenant sequential ticket numbers, safely under concurrency.</summary>
public interface ITicketNumberGenerator
{
    Task<int> NextAsync(Guid tenantId, CancellationToken ct);
}
