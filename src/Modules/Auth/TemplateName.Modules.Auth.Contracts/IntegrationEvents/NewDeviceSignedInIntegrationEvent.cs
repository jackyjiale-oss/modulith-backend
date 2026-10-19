using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Contracts.IntegrationEvents;

/// <summary>
/// A user signed in from a device (device name and user agent) none of their earlier sessions used; the user should be told in case it
/// was not them. It carries no client-supplied text: not the device name and not the user agent, which anyone could have typed.
/// </summary>
/// <param name="Id">The Auth outbox message id of the domain event behind it; the same on every republish, so a consumer inbox recognizes a retry.</param>
/// <param name="OccurredAt">When the user signed in (UTC).</param>
/// <param name="UserId">The user who signed in. Look up the address and language with <c>IUserContactDirectory</c>.</param>
/// <param name="SessionId">The new session.</param>
/// <param name="IpAddress">The address the server observed for the sign-in; null when it observed none.</param>
public sealed record NewDeviceSignedInIntegrationEvent(Guid Id, DateTimeOffset OccurredAt, Guid UserId, Guid SessionId, string? IpAddress) : IIntegrationEvent;
