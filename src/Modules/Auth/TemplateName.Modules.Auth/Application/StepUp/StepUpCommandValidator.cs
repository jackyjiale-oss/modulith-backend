using FluentValidation;
using Microsoft.Extensions.Options;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.ExternalLogins;
using TemplateName.Modules.Auth.Application.Passwords;
using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.Modules.Auth.Application.StepUp;

/// <summary>
/// Runs in the validation decorator, before the handler, so a request without exactly one proof, or with an over-long one (a 1 MB
/// password included), is refused before any lookup or hashing. A blank value counts as absent. The password has no minimum (as at
/// login, Ruling R14: UTF-16 characters, not trimmed, not normalized); the code is not checked for six digits here, because a
/// malformed code is a wrong code and counts like one. A provider's proof (<c>provider</c> with an ID token and its nonce, or a Facebook
/// access token) counts as one proof and follows the rules of <see cref="ExternalProofRules"/>.
/// </summary>
internal sealed class StepUpCommandValidator : AbstractValidator<StepUpCommand>
{
    /// <summary>The key of the "exactly one proof" error in the validation problem's <c>errors</c>.</summary>
    internal const string ProofPropertyName = "proof";

    /// <summary>The longest code accepted; a TOTP code has 6 digits.</summary>
    internal const int MaxCodeLength = 16;

    /// <summary>The longest recovery code accepted; one is 19 characters as shown, more with spaces.</summary>
    internal const int MaxRecoveryCodeLength = 32;

    public StepUpCommandValidator(IOptions<PasswordOptions> passwordOptions, IExternalProviderCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var passwords = passwordOptions.Value;

        RuleFor(command => command)
            .Must(command => Count(command) == 1)
            .OverridePropertyName(ProofPropertyName)
            .WithMessage("Exactly one of 'password', 'code', 'recoveryCode' and a provider proof ('provider' with its token) is required.");

        RuleFor(command => command.Password)
            .MaximumLength(passwords.MaxLength);

        RuleFor(command => command.Code)
            .MaximumLength(MaxCodeLength);

        RuleFor(command => command.RecoveryCode)
            .MaximumLength(MaxRecoveryCodeLength);

        // The provider's members are one proof, checked by the rules login and link share, and only once any of them is present: the
        // request is then a provider proof (a stray nonce included), so it needs its provider and its token. An unknown and a disabled
        // provider both pass here and get the handler's one 404.
        When(command => command.HasProviderProof, () =>
        {
            RuleFor(command => command.Provider)
                .NotEmpty()
                .MaximumLength(UserExternalLogin.ProviderMaxLength);

            this.AddProofRules(
                catalog,
                command => command.Provider ?? string.Empty,
                command => command.IdToken,
                command => command.AccessToken,
                command => command.Nonce);
        });
    }

    private static int Count(StepUpCommand command) =>
        (string.IsNullOrWhiteSpace(command.Password) ? 0 : 1)
        + (string.IsNullOrWhiteSpace(command.Code) ? 0 : 1)
        + (string.IsNullOrWhiteSpace(command.RecoveryCode) ? 0 : 1)
        + (command.HasProviderProof ? 1 : 0);
}
