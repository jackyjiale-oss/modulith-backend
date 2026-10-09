using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Domain.Sessions.Events;

/// <summary>A refresh token that was already used (or revoked) was presented again, so the session was revoked; a handler records it in the audit log.</summary>
internal sealed record RefreshTokenReuseDetectedDomainEvent(Guid UserId, Guid SessionId) : IDomainEvent;
