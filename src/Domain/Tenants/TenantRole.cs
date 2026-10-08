namespace AISupportOps.Domain.Tenants;

/// <summary>
/// Roles within an organization, ordered by privilege (higher value = more privilege).
/// </summary>
public enum TenantRole
{
    Viewer = 0,
    Agent = 1,
    Admin = 2,
    Owner = 3,
}
