using TemplateName.Application.Common.Messaging;
using TemplateName.SharedKernel;

namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>
/// Stands in for the consumers of <typeparamref name="TEvent"/> until the module that consumes it exists: it records every event it
/// receives in <see cref="IntegrationEventRecorder"/>. The integration test factory registers one per Auth integration event, the way
/// <c>AddApplicationHandlers</c> registers a consumer (as its own type, with an <see cref="IntegrationEventHandlerRegistration"/>).
/// </summary>
internal sealed class RecordingIntegrationEventHandler<TEvent>(IntegrationEventRecorder recorder) : IIntegrationEventHandler<TEvent>
    where TEvent : IIntegrationEvent
{
    public Task HandleAsync(TEvent integrationEvent, CancellationToken cancellationToken)
    {
        recorder.Record(integrationEvent);
        return Task.CompletedTask;
    }
}
