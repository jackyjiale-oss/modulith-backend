using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Domain.Users.Events;

/// <summary>A user linked an external provider login. It carries the provider's name only: no subject, token or address.</summary>
internal sealed record ExternalLoginLinkedDomainEvent(Guid UserId, string Provider) : IDomainEvent;
