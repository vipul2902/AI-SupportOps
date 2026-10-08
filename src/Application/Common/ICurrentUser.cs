using AISupportOps.Domain.Tenants;

namespace AISupportOps.Application.Common;

/// <summary>The tenant the current operation is scoped to. Null means "no tenant": tenant data is invisible.</summary>
public interface ITenantContext
{
    Guid? TenantId { get; }
}

/// <summary>The authenticated caller, resolved from the access token.</summary>
public interface ICurrentUser : ITenantContext
{
    Guid? UserId { get; }

    TenantRole? Role { get; }
}

public static class CurrentUserExtensions
{
    public static Guid RequireUserId(this ICurrentUser user) =>
        user.UserId ?? throw new UnauthorizedException("Authentication is required.");

    public static Guid RequireTenantId(this ICurrentUser user) =>
        user.TenantId ?? throw new ForbiddenException("No organization is selected.");
}
