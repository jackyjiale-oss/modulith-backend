using TemplateName.SharedKernel;

namespace TemplateName.IntegrationTests.Persistence;

/// <summary>Raised by <see cref="TestAggregate.Note"/>, which changes no state; the outbox tests check it still reaches the outbox.</summary>
internal sealed record TestAggregateNotedDomainEvent(Guid Id) : IDomainEvent;
