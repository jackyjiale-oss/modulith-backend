using TemplateName.Application.Common.Messaging;
using TemplateName.IntegrationTests.Persistence;

namespace TemplateName.IntegrationTests.Outbox;

/// <summary>Waits at <see cref="HandlerGate"/>; passes straight through unless a test has closed the gate.</summary>
internal sealed class GateTestEventHandler(HandlerGate gate) : IDomainEventHandler<TestAggregateCreatedDomainEvent>
{
    public Task HandleAsync(TestAggregateCreatedDomainEvent domainEvent, CancellationToken cancellationToken)
        => gate.PassAsync(domainEvent.Id, cancellationToken);
}
