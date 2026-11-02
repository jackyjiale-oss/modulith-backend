using System.Linq.Expressions;
using FluentValidation;
using TemplateName.Modules.Auth.Application.Abstractions;

namespace TemplateName.Modules.Auth.Application.ExternalLogins;

/// <summary>
/// The rules every command that carries a provider's proof shares (login and link): exactly one of an ID token and an access token, each at
/// most 8192 characters, an access token only for an <b>enabled</b> provider that is Facebook, and a 16 to 256 character nonce with an ID
/// token. A blank value counts as absent. The answer depends on the request and the configuration alone, never on an account.
/// </summary>
internal static class ExternalProofRules
{
    /// <summary>The key of the "exactly one proof" error in the validation problem's <c>errors</c>.</summary>
    internal const string ProofPropertyName = "proof";

    /// <summary>Adds the proof rules to <paramref name="validator"/>; <paramref name="provider"/> is the route's provider name.</summary>
    internal static void AddProofRules<T>(
        this AbstractValidator<T> validator,
        IExternalProviderCatalog catalog,
        Func<T, string> provider,
        Expression<Func<T, string?>> idToken,
        Expression<Func<T, string?>> accessToken,
        Expression<Func<T, string?>> nonce)
    {
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(provider);

        var idTokenOf = idToken.Compile();
        var accessTokenOf = accessToken.Compile();
        var nonceOf = nonce.Compile();

        validator.RuleFor(command => command)
            .Must(command => string.IsNullOrWhiteSpace(idTokenOf(command)) != string.IsNullOrWhiteSpace(accessTokenOf(command)))
            .OverridePropertyName(ProofPropertyName)
            .WithMessage("Exactly one of 'idToken' and 'accessToken' is required.");

        validator.RuleFor(idToken)
            .MaximumLength(ExternalProof.MaxTokenLength);

        validator.RuleFor(accessToken)
            .Cascade(CascadeMode.Stop)
            .MaximumLength(ExternalProof.MaxTokenLength)
            .Must((command, _) => catalog.Find(provider(command)) is not { Kind: not ExternalProviderKind.Facebook })
            .When(command => !string.IsNullOrWhiteSpace(accessTokenOf(command)))
            .WithMessage("'accessToken' is accepted only by the Facebook provider; send an 'idToken' and its 'nonce'.");

        validator.RuleFor(nonce)
            .NotEmpty()
            .When(command => !string.IsNullOrWhiteSpace(idTokenOf(command)))
            .WithMessage("'nonce' is required with an 'idToken'.");

        validator.RuleFor(nonce)
            .Length(ExternalProof.MinNonceLength, ExternalProof.MaxNonceLength)
            .When(command => !string.IsNullOrWhiteSpace(nonceOf(command)));
    }
}
