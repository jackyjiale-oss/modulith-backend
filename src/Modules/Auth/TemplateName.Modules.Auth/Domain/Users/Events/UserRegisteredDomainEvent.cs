using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Domain.Users.Events;

/// <summary>A user registered; a handler sends the verification email.</summary>
internal sealed record UserRegisteredDomainEvent(Guid UserId, string Email, string Locale) : IDomainEvent;
