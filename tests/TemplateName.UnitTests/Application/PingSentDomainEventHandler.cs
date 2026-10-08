using TemplateName.Application.Common.Messaging;

namespace TemplateName.UnitTests.Application;

internal sealed class PingSentDomainEventHandler : IDomainEventHandler<PingSentDomainEvent>
{
    public Task HandleAsync(PingSentDomainEvent domainEvent, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
