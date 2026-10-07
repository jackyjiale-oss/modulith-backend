using TemplateName.SharedKernel;

namespace TemplateName.IntegrationTests.Persistence;

/// <summary>Raised by <see cref="TestAggregate.Create"/>; the outbox tests dispatch it.</summary>
internal sealed record TestAggregateCreatedDomainEvent(Guid Id) : IDomainEvent;
