using AISupportOps.Domain.Identity;
using AISupportOps.Domain.Tenants;

namespace AISupportOps.Application.Identity;

public interface IPasswordHasher
{
    string Hash(User user, string password);

    bool Verify(User user, string password);
}

public sealed record AccessToken(string Token, DateTimeOffset ExpiresAt);

public interface IAccessTokenIssuer
{
    AccessToken Issue(User user, Guid tenantId, TenantRole role);
}
