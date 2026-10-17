using TemplateName.Application.Common.Messaging;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Auth.IntegrationEvents;

/// <summary>An <see cref="IIntegrationEventPublisher"/> that keeps every published event in memory, in order.</summary>
internal sealed class RecordingIntegrationEventPublisher : IIntegrationEventPublisher
{
    public List<IIntegrationEvent> Published { get; } = [];

    public Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken)
        where TEvent : IIntegrationEvent
    {
        Published.Add(integrationEvent);
        return Task.CompletedTask;
    }
}
