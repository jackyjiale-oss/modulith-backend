using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Contracts.IntegrationEvents;

/// <summary>Someone tried to register an address that already has an account; its owner should be told.</summary>
/// <param name="Id">The Auth outbox message id of the domain event behind it; the same on every republish, so a consumer inbox recognizes a retry.</param>
/// <param name="OccurredAt">When the attempt happened (UTC).</param>
/// <param name="UserId">The owner of the existing account. Look up the address and language with <c>IUserContactDirectory</c>.</param>
public sealed record RegistrationAttemptedIntegrationEvent(Guid Id, DateTimeOffset OccurredAt, Guid UserId) : IIntegrationEvent;
