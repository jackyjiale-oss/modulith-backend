using TemplateName.Application.Common.Messaging;
using TemplateName.IntegrationTests.Persistence;

namespace TemplateName.IntegrationTests.Outbox;

/// <summary>Throws while <see cref="FlakySwitch"/> says so; a <see cref="FlakySwitch.FailNext"/> failure happens once.</summary>
internal sealed class FlakyTestEventHandler(FlakySwitch flakySwitch) : IDomainEventHandler<TestAggregateCreatedDomainEvent>
{
    public Task HandleAsync(TestAggregateCreatedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        if (flakySwitch.FailNext || flakySwitch.FailAlways)
        {
            flakySwitch.FailNext = false;
            throw new InvalidOperationException($"Flaky handler failed for {domainEvent.Id}.");
        }

        return Task.CompletedTask;
    }
}
