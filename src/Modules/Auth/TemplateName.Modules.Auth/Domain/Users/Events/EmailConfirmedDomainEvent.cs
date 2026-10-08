using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Domain.Users.Events;

/// <summary>A user confirmed their email address.</summary>
internal sealed record EmailConfirmedDomainEvent(Guid UserId, string Email) : IDomainEvent;
