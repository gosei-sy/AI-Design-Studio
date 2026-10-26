namespace AIDesignStudio.SharedKernel;

/// <summary>
/// Base class for entities identified by a <see cref="Guid"/>.
/// Equality is identity-based, not structural.
/// </summary>
public abstract class Entity
{
    public Guid Id { get; protected set; } = Guid.CreateVersion7();

    public override bool Equals(object? obj) =>
        obj is Entity other && other.GetType() == GetType() && other.Id == Id;

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);
}

/// <summary>
/// Marks the consistency boundary of a module. Only aggregate roots may be
/// referenced across module boundaries, and only by id.
/// </summary>
public abstract class AggregateRoot : Entity
{
    public DateTimeOffset CreatedAt { get; protected set; }
    public DateTimeOffset? UpdatedAt { get; protected set; }

    protected void Touch(IClock clock) => UpdatedAt = clock.UtcNow;
}
