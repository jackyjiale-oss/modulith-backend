using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Contracts.IntegrationEvents;

/// <summary>Repeated failed sign-ins locked a user account for a while; the user should be told.</summary>
/// <param name="Id">The Auth outbox message id of the domain event behind it; the same on every republish, so a consumer inbox recognizes a retry.</param>
/// <param name="OccurredAt">When the account was locked (UTC).</param>
/// <param name="UserId">The locked user. Look up the address and language with <c>IUserContactDirectory</c>.</param>
/// <param name="LockoutEnd">When the lockout ends (UTC).</param>
public sealed record UserLockedOutIntegrationEvent(Guid Id, DateTimeOffset OccurredAt, Guid UserId, DateTimeOffset LockoutEnd) : IIntegrationEvent;
