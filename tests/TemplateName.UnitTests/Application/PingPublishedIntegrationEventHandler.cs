using TemplateName.Application.Common.Messaging;

namespace TemplateName.UnitTests.Application;

internal sealed class PingPublishedIntegrationEventHandler : IIntegrationEventHandler<PingPublishedIntegrationEvent>
{
    public Task HandleAsync(PingPublishedIntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        integrationEvent.Handlers.Add(nameof(PingPublishedIntegrationEventHandler));
        return Task.CompletedTask;
    }
}
