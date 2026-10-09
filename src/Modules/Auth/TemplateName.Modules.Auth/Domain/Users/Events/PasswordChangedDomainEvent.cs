using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Domain.Users.Events;

/// <summary>A user's password was changed or reset; a handler tells the owner.</summary>
internal sealed record PasswordChangedDomainEvent(Guid UserId, string Email, string Locale) : IDomainEvent;
