using AISupportOps.Domain.Common;

namespace AISupportOps.Domain.Identity;

/// <summary>
/// A person who can sign in. Users are global; access to data is granted per tenant
/// through <see cref="Tenants.TenantMembership"/>.
/// </summary>
public sealed class User : Entity
{
    public const int EmailMaxLength = 256;
    public const int DisplayNameMaxLength = 100;

    private User()
    {
    }

    public User(string email, string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        Email = email.Trim();
        NormalizedEmail = NormalizeEmail(email);
        DisplayName = displayName.Trim();
    }

    public string Email { get; private set; } = string.Empty;

    /// <summary>Case-insensitive lookup key; unique across the platform.</summary>
    public string NormalizedEmail { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public string PasswordHash { get; private set; } = string.Empty;

    public void SetPasswordHash(string passwordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PasswordHash = passwordHash;
    }

    public static string NormalizeEmail(string email) => email.Trim().ToUpperInvariant();
}
