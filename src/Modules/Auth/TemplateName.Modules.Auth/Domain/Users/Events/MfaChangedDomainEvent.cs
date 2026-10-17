using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Domain.Users.Events;

/// <summary>A user's two-step verification changed, or a recovery code was used to sign in. It carries no code, secret or address.</summary>
internal sealed record MfaChangedDomainEvent(Guid UserId, MfaChange Change) : IDomainEvent;
