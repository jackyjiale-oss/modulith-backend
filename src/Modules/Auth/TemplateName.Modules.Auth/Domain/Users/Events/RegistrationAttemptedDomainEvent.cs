using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Domain.Users.Events;

/// <summary>Someone tried to register an address that already has an account; a handler tells the owner.</summary>
internal sealed record RegistrationAttemptedDomainEvent(Guid UserId, string Email, string Locale) : IDomainEvent;
