using AISupportOps.Domain.Common;
using AISupportOps.Domain.Identity;

namespace AISupportOps.Domain.Tickets;

/// <summary>An end customer of the tenant (the person or company being supported). Not a platform user.</summary>
public sealed class Customer : Entity, ITenantOwned
{
    public const int NameMaxLength = 200;

    private Customer()
    {
    }

    public Customer(Guid tenantId, string name, string email, string? company)
    {
        TenantId = tenantId;
        Update(name, email, company);
    }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string Email { get; private set; } = string.Empty;

    /// <summary>Unique per tenant (case-insensitive).</summary>
    public string NormalizedEmail { get; private set; } = string.Empty;

    public string? Company { get; private set; }

    public void Update(string name, string email, string? company)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        Name = name.Trim();
        Email = email.Trim();
        NormalizedEmail = User.NormalizeEmail(email);
        Company = string.IsNullOrWhiteSpace(company) ? null : company.Trim();
    }
}
