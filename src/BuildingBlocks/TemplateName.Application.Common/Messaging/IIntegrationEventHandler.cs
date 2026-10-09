using TemplateName.SharedKernel;

namespace TemplateName.Application.Common.Messaging;

/// <summary>
/// Consumes an integration event another module published. Runs in its own DI scope, at least once: a handler records the event in
/// its module's inbox in the same save as its own rows, so a repeat has no effect. It only writes rows (anything slow is queued),
/// because it runs inside the publishing module's outbox dispatch (ADR 0018).
/// </summary>
/// <typeparam name="TEvent">The integration event type, from the publishing module's <c>.Contracts</c> project.</typeparam>
public interface IIntegrationEventHandler<in TEvent>
    where TEvent : IIntegrationEvent
{
    Task HandleAsync(TEvent integrationEvent, CancellationToken cancellationToken);
}
