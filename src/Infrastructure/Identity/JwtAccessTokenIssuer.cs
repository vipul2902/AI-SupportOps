using System.Security.Claims;
using System.Text;
using AISupportOps.Application.Identity;
using AISupportOps.Domain.Identity;
using AISupportOps.Domain.Tenants;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AISupportOps.Infrastructure.Identity;

/// <summary>Issues short-lived HS256 JWTs scoped to a single tenant.</summary>
internal sealed class JwtAccessTokenIssuer(IOptions<AuthOptions> options, TimeProvider time) : IAccessTokenIssuer
{
    private readonly JsonWebTokenHandler _handler = new();

    public AccessToken Issue(User user, Guid tenantId, TenantRole role)
    {
        var settings = options.Value;
        var now = time.GetUtcNow();
        var expires = now + settings.AccessTokenLifetime;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(JwtSigningKey.Create(settings), SecurityAlgorithms.HmacSha256),
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, user.Email),
                new Claim(JwtRegisteredClaimNames.Name, user.DisplayName),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                new Claim(AppClaims.TenantId, tenantId.ToString()),
                new Claim(AppClaims.Role, role.ToString()),
            ]),
        };

        return new AccessToken(_handler.CreateToken(descriptor), expires);
    }
}

/// <summary>Shared by the issuer and the API's token validation so both use the same key.</summary>
public static class JwtSigningKey
{
    public static SymmetricSecurityKey Create(AuthOptions settings) =>
        new(Encoding.UTF8.GetBytes(settings.SigningKey));
}

/// <summary>Custom claim names. Kept short because they travel on every request.</summary>
public static class AppClaims
{
    public const string TenantId = "tid";
    public const string Role = "role";
}
