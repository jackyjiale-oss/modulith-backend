using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Contracts.IntegrationEvents;

/// <summary>
/// A user's two-step verification changed, or a recovery code was used; the user should be told in case it was not them. It carries
/// no secret: no authenticator secret, recovery code, token or device name.
/// </summary>
/// <param name="Id">The Auth outbox message id of the domain event behind it; the same on every republish, so a consumer inbox recognizes a retry.</param>
/// <param name="OccurredAt">When the change happened (UTC).</param>
/// <param name="UserId">The user whose two-step verification changed. Look up the address and language with <c>IUserContactDirectory</c>.</param>
/// <param name="Change">What happened.</param>
public sealed record MfaChangedIntegrationEvent(Guid Id, DateTimeOffset OccurredAt, Guid UserId, MfaChangeKind Change) : IIntegrationEvent;
