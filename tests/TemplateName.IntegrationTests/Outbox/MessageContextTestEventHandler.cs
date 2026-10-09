using TemplateName.Application.Common.Messaging;
using TemplateName.IntegrationTests.Persistence;

namespace TemplateName.IntegrationTests.Outbox;

/// <summary>
/// Records the <see cref="IOutboxMessageContext"/> it runs under, then fails once if <see cref="FlakySwitch.FailNext"/> is set, so the
/// outbox retries it.
/// </summary>
internal sealed class MessageContextTestEventHandler(
    IOutboxMessageContext messageContext,
    MessageContextRecorder recorder,
    FlakySwitch flakySwitch) : IDomainEventHandler<TestAggregateNotedDomainEvent>
{
    public Task HandleAsync(TestAggregateNotedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        recorder.Record(messageContext.MessageId, messageContext.OccurredAt);

        if (flakySwitch.FailNext)
        {
            flakySwitch.FailNext = false;
            throw new InvalidOperationException($"Message context handler failed for {domainEvent.Id}.");
        }

        return Task.CompletedTask;
    }
}
