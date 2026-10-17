using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Contracts.IntegrationEvents;

/// <summary>A user password was changed or reset; the user should be told in case it was not them.</summary>
/// <param name="Id">The Auth outbox message id of the domain event behind it; the same on every republish, so a consumer inbox recognizes a retry.</param>
/// <param name="OccurredAt">When the password changed (UTC).</param>
/// <param name="UserId">The user whose password changed. Look up the address and language with <c>IUserContactDirectory</c>.</param>
public sealed record PasswordChangedIntegrationEvent(Guid Id, DateTimeOffset OccurredAt, Guid UserId) : IIntegrationEvent;
