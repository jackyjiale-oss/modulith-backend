using TemplateName.Application.Common.Messaging;
using TemplateName.IntegrationTests.Persistence;

namespace TemplateName.IntegrationTests.Outbox;

/// <summary>Records every <see cref="TestAggregateCreatedDomainEvent"/> it receives.</summary>
internal sealed class RecordingTestEventHandler(EventRecorder recorder) : IDomainEventHandler<TestAggregateCreatedDomainEvent>
{
    public Task HandleAsync(TestAggregateCreatedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        recorder.Record(domainEvent);
        return Task.CompletedTask;
    }
}
