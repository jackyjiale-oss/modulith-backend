using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Contracts.IntegrationEvents;

/// <summary>A refresh token that was already used (or revoked) was presented again, which suggests it was stolen; its session was revoked.</summary>
/// <param name="Id">The Auth outbox message id of the domain event behind it; the same on every republish, so a consumer inbox recognizes a retry.</param>
/// <param name="OccurredAt">When the reuse was detected (UTC).</param>
/// <param name="UserId">The user the session belongs to. Look up the address and language with <c>IUserContactDirectory</c>.</param>
/// <param name="SessionId">The revoked session.</param>
public sealed record RefreshTokenReuseDetectedIntegrationEvent(Guid Id, DateTimeOffset OccurredAt, Guid UserId, Guid SessionId) : IIntegrationEvent;
