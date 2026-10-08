namespace AISupportOps.Domain.Common;

/// <summary>
/// Marks an entity as belonging to exactly one tenant. The persistence layer applies a
/// global query filter and a write guard to every type implementing this interface.
/// </summary>
public interface ITenantOwned
{
    Guid TenantId { get; }
}
