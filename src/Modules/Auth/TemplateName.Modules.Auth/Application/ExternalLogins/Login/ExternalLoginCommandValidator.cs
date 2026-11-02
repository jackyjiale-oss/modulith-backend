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
    internal const string ProofPropertyName = ExternalProofRules.ProofPropertyName;

    public ExternalLoginCommandValidator(IExternalProviderCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        this.AddProofRules(
            catalog,
            command => command.Provider,
            command => command.IdToken,
            command => command.AccessToken,
            command => command.Nonce);

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
