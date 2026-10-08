using AISupportOps.Domain.Identity;
using AspNetIdentity = Microsoft.AspNetCore.Identity;

namespace AISupportOps.Infrastructure.Identity;

/// <summary>
/// Adapter over ASP.NET Core Identity's hasher (PBKDF2-HMAC-SHA512, salted, versioned format).
/// Reusing a vetted implementation beats writing password hashing by hand.
/// </summary>
internal sealed class PasswordHasher : Application.Identity.IPasswordHasher
{
    private readonly AspNetIdentity.PasswordHasher<User> _inner = new();

    public string Hash(User user, string password) => _inner.HashPassword(user, password);

    public bool Verify(User user, string password) =>
        _inner.VerifyHashedPassword(user, user.PasswordHash, password) != AspNetIdentity.PasswordVerificationResult.Failed;
}
