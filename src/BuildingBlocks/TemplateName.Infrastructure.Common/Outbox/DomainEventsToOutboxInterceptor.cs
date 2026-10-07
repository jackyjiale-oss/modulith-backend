using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TemplateName.SharedKernel;

namespace TemplateName.Infrastructure.Common.Outbox;

/// <summary>
/// Writes the domain events of every tracked <see cref="IHasDomainEvents"/> entity as <see cref="OutboxMessage"/> rows in the same
/// save, then clears them. The context must map the outbox (<c>ApplyOutbox</c>) to save entities that raise events.
/// </summary>
internal sealed class DomainEventsToOutboxInterceptor(TimeProvider timeProvider) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        AddOutboxMessages(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        AddOutboxMessages(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void AddOutboxMessages(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        // Materialized: adding outbox rows while enumerating the change tracker is not allowed.
        var sources = context.ChangeTracker.Entries<IHasDomainEvents>()
            .Select(entry => entry.Entity)
            .Where(entity => entity.DomainEvents.Count > 0)
            .ToList();

        if (sources.Count == 0)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var messages = context.Set<OutboxMessage>();

        foreach (var source in sources)
        {
            foreach (var domainEvent in source.DomainEvents)
            {
                var eventType = domainEvent.GetType();
                messages.Add(new OutboxMessage
                {
                    Id = SequentialGuid.Create(now),
                    Type = eventType.FullName!,
                    Content = JsonSerializer.Serialize(domainEvent, eventType),
                    OccurredAt = now.UtcDateTime,
                });
            }

            source.ClearDomainEvents();
        }
    }
}
