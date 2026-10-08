using AISupportOps.Domain.Common;

namespace AISupportOps.Domain.Tenants;

/// <summary>An organization using the platform. The root of tenant isolation.</summary>
public sealed class Tenant : Entity
{
    public const int NameMaxLength = 100;
    public const int SlugMaxLength = 120;

    private Tenant()
    {
    }

    public Tenant(string name, string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        Rename(name);
        Slug = slug;
    }

    public string Name { get; private set; } = string.Empty;

    public string Slug { get; private set; } = string.Empty;

    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
    }
}
