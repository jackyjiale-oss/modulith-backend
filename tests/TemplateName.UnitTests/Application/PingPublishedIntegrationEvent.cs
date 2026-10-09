using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Application;

/// <summary>An integration event whose handlers record themselves in <see cref="Handlers"/>.</summary>
internal sealed record PingPublishedIntegrationEvent(Guid Id, DateTimeOffset OccurredAt, List<string> Handlers) : IIntegrationEvent;
