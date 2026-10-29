using TemplateName.Application.Common.Auditing;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Audit.Application.Abstractions;
using TemplateName.Modules.Audit.Domain.Trails;

namespace TemplateName.Modules.Audit.Application.Trails.Record;

/// <summary>
/// Writes one change set into the audit trail, exactly once (ADR 0018): an event this consumer has already processed is skipped;
/// otherwise one <see cref="AuditTrail"/> per change and the inbox record are saved together in one save. When a delivery of the same
/// event that raced past the check saved first, the save fails on the inbox key and nothing is written. Any other failure is thrown, so
/// the publishing module's outbox retries the message. The handler only writes rows.
/// </summary>
internal sealed class EntityChangesRecordedIntegrationEventHandler(
    IInbox inbox,
    IAuditTrailRepository trails,
    IUnitOfWork unitOfWork)
    : IIntegrationEventHandler<EntityChangesRecordedIntegrationEvent>
{
    private static readonly string Consumer = typeof(EntityChangesRecordedIntegrationEventHandler).FullName!;

    public async Task HandleAsync(EntityChangesRecordedIntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        if (await inbox.HasProcessedAsync(integrationEvent.Id, Consumer, cancellationToken))
        {
            return;
        }

        foreach (var change in integrationEvent.Changes)
        {
            trails.Add(AuditTrail.Create(
                integrationEvent.Id,
                integrationEvent.OccurredAt,
                integrationEvent.UserId,
                integrationEvent.SessionId,
                integrationEvent.Module,
                change,
                integrationEvent.TraceId));
        }

        inbox.Record(integrationEvent.Id, Consumer);

        // False means another delivery saved this change set first; the event counts as processed either way.
        await unitOfWork.SaveChangesUnlessInboxDuplicateAsync(cancellationToken);
    }
}
