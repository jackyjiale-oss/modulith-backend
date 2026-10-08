namespace TemplateName.SharedKernel;

/// <summary>Base type for domain objects that have an identity.</summary>
/// <typeparam name="TId">The identifier type.</typeparam>
public abstract class Entity<TId>
    where TId : notnull
{
    public TId Id { get; protected set; } = default!;
}
