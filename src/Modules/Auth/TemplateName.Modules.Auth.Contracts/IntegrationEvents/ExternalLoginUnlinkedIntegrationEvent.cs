using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Contracts.IntegrationEvents;

/// <summary>
/// A user removed a social-login provider from their account; the user should be told in case it was not them. It carries no subject,
/// token or address of the provider.
/// </summary>
/// <param name="Id">The Auth outbox message id of the domain event behind it; the same on every republish, so a consumer inbox recognizes a retry.</param>
/// <param name="OccurredAt">When the provider was removed (UTC).</param>
/// <param name="UserId">The user whose account lost the sign-in method. Look up the address and language with <c>IUserContactDirectory</c>.</param>
/// <param name="Provider">The provider's name as it was stored, lower-case (<c>google</c>).</param>
/// <param name="ProviderDisplayName">The name to show a person (<c>Google</c>): the configured display name, or <paramref name="Provider"/> itself when the provider is no longer configured (a switched-off provider's login can still be removed).</param>
public sealed record ExternalLoginUnlinkedIntegrationEvent(Guid Id, DateTimeOffset OccurredAt, Guid UserId, string Provider, string ProviderDisplayName) : IIntegrationEvent;
