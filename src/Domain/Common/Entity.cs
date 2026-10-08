namespace AISupportOps.Domain.Common;

/// <summary>
/// Base type for all persisted entities. Timestamps are stamped by the persistence layer,
/// so domain code never has to remember to update them.
/// </summary>
public abstract class Entity
{
    public Guid Id { get; protected set; } = Guid.CreateVersion7();

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
