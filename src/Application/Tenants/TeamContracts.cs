using System.ComponentModel.DataAnnotations;
using AISupportOps.Domain.Identity;
using AISupportOps.Domain.Tenants;

namespace AISupportOps.Application.Tenants;

public sealed record MemberResponse(Guid UserId, string Email, string DisplayName, TenantRole Role, DateTimeOffset JoinedAt);

public sealed record ChangeRoleRequest([Required, EnumDataType(typeof(TenantRole))] TenantRole Role);

public sealed record CreateInvitationRequest(
    [Required, EmailAddress, MaxLength(User.EmailMaxLength)] string Email,
    [Required, EnumDataType(typeof(TenantRole))] TenantRole Role);

public sealed record InvitationResponse(Guid Id, string Email, TenantRole Role, DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt);

/// <summary>
/// Returned once at creation. In production the token is delivered by email instead;
/// it is returned here so the flow is usable before an email provider is configured.
/// </summary>
public sealed record InvitationCreatedResponse(InvitationResponse Invitation, string Token);

public sealed record TenantResponse(Guid Id, string Name, string Slug, DateTimeOffset CreatedAt);

public sealed record RenameTenantRequest([Required, MaxLength(Tenant.NameMaxLength)] string Name);
