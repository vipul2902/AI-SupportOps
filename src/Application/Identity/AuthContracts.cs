using System.ComponentModel.DataAnnotations;
using AISupportOps.Domain.Identity;
using AISupportOps.Domain.Tenants;

namespace AISupportOps.Application.Identity;

public static class PasswordRules
{
    public const int MinLength = 12;
    public const int MaxLength = 128;
}

public sealed record RegisterRequest(
    [Required, EmailAddress, MaxLength(User.EmailMaxLength)] string Email,
    [Required, StringLength(PasswordRules.MaxLength, MinimumLength = PasswordRules.MinLength)] string Password,
    [Required, MaxLength(User.DisplayNameMaxLength)] string DisplayName,
    [Required, MaxLength(Tenant.NameMaxLength)] string OrganizationName);

public sealed record LoginRequest(
    [Required, EmailAddress] string Email,
    [Required] string Password,
    Guid? TenantId);

/// <summary>Body is optional for browsers: in cookie mode the token arrives as an httpOnly cookie.</summary>
public sealed record RefreshRequest(string? RefreshToken);

public sealed record SwitchTenantRequest([Required] Guid TenantId);

public sealed record AcceptInvitationRequest(
    [Required] string Token,
    [Required, StringLength(PasswordRules.MaxLength, MinimumLength = PasswordRules.MinLength)] string Password,
    [MaxLength(User.DisplayNameMaxLength)] string? DisplayName);

public sealed record AuthResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    Guid TenantId,
    TenantRole Role);

public sealed record MembershipSummary(Guid TenantId, string TenantName, TenantRole Role);

public sealed record MeResponse(
    Guid UserId,
    string Email,
    string DisplayName,
    Guid TenantId,
    TenantRole Role,
    IReadOnlyList<MembershipSummary> Memberships);
