using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.ExternalLogins.Providers;

/// <summary>Lists the enabled sign-in providers, in name order, for a client's sign-in screen.</summary>
internal sealed record ListExternalProvidersQuery : IQuery<IReadOnlyList<ExternalProviderResponse>>;
