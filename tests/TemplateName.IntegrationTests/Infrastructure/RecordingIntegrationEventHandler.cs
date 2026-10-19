using TemplateName.Application.Common.Messaging;
using TemplateName.SharedKernel;

namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>
/// Records every <typeparamref name="TEvent"/> it receives in <see cref="IntegrationEventRecorder"/>, so tests can assert what was
/// published; it runs next to the real consumers (the Notifications module's). The integration test factory registers one per Auth integration event, the way
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
