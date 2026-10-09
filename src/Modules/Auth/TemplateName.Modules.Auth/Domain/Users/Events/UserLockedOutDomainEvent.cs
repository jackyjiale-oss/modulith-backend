using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Domain.Users.Events;

/// <summary>Too many failed sign-ins locked an account until <paramref name="LockoutEnd"/>.</summary>
internal sealed record UserLockedOutDomainEvent(Guid UserId, DateTimeOffset LockoutEnd) : IDomainEvent;
