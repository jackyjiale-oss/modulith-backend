using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Application;

internal sealed record PingSentDomainEvent(string Reason) : IDomainEvent;
