using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Domain.Users.Events;

/// <summary>A user removed an external provider login. It carries the provider's name only: no subject, token or address.</summary>
internal sealed record ExternalLoginUnlinkedDomainEvent(Guid UserId, string Provider) : IDomainEvent;
