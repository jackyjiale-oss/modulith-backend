using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.ExternalLogins.Providers;

/// <summary>
/// Answers from the catalog, which holds only enabled providers: a switched-off provider is not listed, and nothing configured beyond the
/// name, the display name and the kind leaves the server.
/// </summary>
internal sealed class ListExternalProvidersQueryHandler(IExternalProviderCatalog catalog)
    : IQueryHandler<ListExternalProvidersQuery, IReadOnlyList<ExternalProviderResponse>>
{
    public Task<Result<IReadOnlyList<ExternalProviderResponse>>> HandleAsync(ListExternalProvidersQuery query, CancellationToken cancellationToken)
    {
        IReadOnlyList<ExternalProviderResponse> providers =
        [
            .. catalog.Enabled.Select(provider => new ExternalProviderResponse(
                provider.Name,
                provider.DisplayName,
                provider.Kind == ExternalProviderKind.Facebook ? ExternalProviderResponse.FacebookType : ExternalProviderResponse.OidcType)),
        ];

        return Task.FromResult(Result.Success(providers));
    }
}
