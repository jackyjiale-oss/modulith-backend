using System.Globalization;
using FluentValidation;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.Modules.Auth.Application.ExternalLogins.Login;

/// <summary>
/// Runs in the validation decorator, before the handler, so a request without exactly one proof, or with an over-long one (a 1 MB token
/// included), is refused before any provider is asked. A blank value counts as absent. The answer depends on the request and the
/// configuration alone, never on an account: an access token is refused only for an <b>enabled</b> provider that is not Facebook, so an
/// unknown and a disabled provider both pass here and get the handler's one 404.
/// </summary>
internal sealed class ExternalLoginCommandValidator : AbstractValidator<ExternalLoginCommand>
{
    /// <summary>The key of the "exactly one proof" error in the validation problem's <c>errors</c>.</summary>
    internal const string ProofPropertyName = "proof";

    public ExternalLoginCommandValidator(IExternalProviderCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        RuleFor(command => command)
            .Must(command => string.IsNullOrWhiteSpace(command.IdToken) != string.IsNullOrWhiteSpace(command.AccessToken))
            .OverridePropertyName(ProofPropertyName)
            .WithMessage("Exactly one of 'idToken' and 'accessToken' is required.");

        RuleFor(command => command.IdToken)
            .MaximumLength(ExternalProof.MaxTokenLength);

        RuleFor(command => command.AccessToken)
            .Cascade(CascadeMode.Stop)
            .MaximumLength(ExternalProof.MaxTokenLength)
            .Must((command, _) => catalog.Find(command.Provider) is not { Kind: not ExternalProviderKind.Facebook })
            .When(command => !string.IsNullOrWhiteSpace(command.AccessToken))
            .WithMessage("'accessToken' is accepted only by the Facebook provider; send an 'idToken' and its 'nonce'.");

        RuleFor(command => command.Nonce)
            .NotEmpty()
            .When(command => !string.IsNullOrWhiteSpace(command.IdToken))
            .WithMessage("'nonce' is required with an 'idToken'.");

        RuleFor(command => command.Nonce)
            .Length(ExternalProof.MinNonceLength, ExternalProof.MaxNonceLength)
            .When(command => !string.IsNullOrWhiteSpace(command.Nonce));

        RuleFor(command => command.DisplayName)
            .MaximumLength(User.MaxDisplayNameLength);

        RuleFor(command => command.DeviceName)
            .MaximumLength(UserSession.MaxDeviceNameLength);

        RuleFor(command => command.Locale)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MaximumLength(User.MaxLocaleLength)
            .Must(IsPredefinedCulture)
            .When(command => command.Locale is not null)
            .WithMessage("'{PropertyName}' must be a known culture name, such as en, ms or zh-Hans.");
    }

    private static bool IsPredefinedCulture(string? locale)
    {
        try
        {
            return locale is not null && CultureInfo.GetCultureInfo(locale, predefinedOnly: true).Name.Length > 0;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }
}
