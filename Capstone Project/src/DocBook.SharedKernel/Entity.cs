namespace DocBook.SharedKernel;

public abstract class Entity<TId> where TId : struct
{
    protected Entity(TId id) => Id = id;

    // Used by the persistence layer when it rehydrates the entity.
    protected Entity()
    {
    }

    public TId Id { get; private set; }

    public override bool Equals(object? obj) =>
        obj is Entity<TId> other && other.GetType() == GetType() && other.Id.Equals(Id);

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);
}
