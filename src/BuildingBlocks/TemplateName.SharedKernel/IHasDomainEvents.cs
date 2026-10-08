namespace TemplateName.SharedKernel;

/// <summary>An object that collects domain events until the unit of work publishes them.</summary>
public interface IHasDomainEvents
{
    IReadOnlyList<IDomainEvent> DomainEvents { get; }

    void ClearDomainEvents();
}
