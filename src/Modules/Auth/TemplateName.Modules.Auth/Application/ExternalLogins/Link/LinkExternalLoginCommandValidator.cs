using FluentValidation;
using TemplateName.Modules.Auth.Application.Abstractions;

namespace TemplateName.Modules.Auth.Application.ExternalLogins.Link;

/// <summary>
/// Runs in the validation decorator, before the handler, so a request without exactly one proof, or with an over-long one, is refused
/// before any provider is asked (the rules of <see cref="ExternalProofRules"/>, the login's). An unknown and a disabled provider both
/// pass here and get the handler's one 404.
/// </summary>
internal sealed class LinkExternalLoginCommandValidator : AbstractValidator<LinkExternalLoginCommand>
{
    public LinkExternalLoginCommandValidator(IExternalProviderCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        this.AddProofRules(
            catalog,
            command => command.Provider,
            command => command.IdToken,
            command => command.AccessToken,
            command => command.Nonce);
    }
}
