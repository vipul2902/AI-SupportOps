using System.Security.Claims;
using AISupportOps.Application.Common;
using AISupportOps.Domain.Tenants;
using AISupportOps.Infrastructure.Identity;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AISupportOps.Api.Auth;

/// <summary>
/// Resolves the caller from the validated JWT. Outside an HTTP request (or when anonymous)
/// every property is null, which makes tenant-owned data invisible.
/// </summary>
internal sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal =>
        accessor.HttpContext?.User is { Identity.IsAuthenticated: true } user ? user : null;

    public Guid? UserId => ParseGuid(JwtRegisteredClaimNames.Sub);

    public Guid? TenantId => ParseGuid(AppClaims.TenantId);

    public TenantRole? Role =>
        Enum.TryParse<TenantRole>(Principal?.FindFirstValue(AppClaims.Role), out var role) ? role : null;

    private Guid? ParseGuid(string claimType) =>
        Guid.TryParse(Principal?.FindFirstValue(claimType), out var value) ? value : null;
}
