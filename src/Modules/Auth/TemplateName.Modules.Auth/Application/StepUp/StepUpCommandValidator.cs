using FluentValidation;
using Microsoft.Extensions.Options;
using TemplateName.Modules.Auth.Application.Passwords;

namespace TemplateName.Modules.Auth.Application.StepUp;

/// <summary>
/// Runs in the validation decorator, before the handler, so a request without exactly one proof, or with an over-long one (a 1 MB
/// password included), is refused before any lookup or hashing. A blank value counts as absent. The password has no minimum (as at
/// login, Ruling R14: UTF-16 characters, not trimmed, not normalized); the code is not checked for six digits here, because a
/// malformed code is a wrong code and counts like one.
/// </summary>
internal sealed class StepUpCommandValidator : AbstractValidator<StepUpCommand>
{
    /// <summary>The key of the "exactly one proof" error in the validation problem's <c>errors</c>.</summary>
    internal const string ProofPropertyName = "proof";

    /// <summary>The longest code accepted; a TOTP code has 6 digits.</summary>
    internal const int MaxCodeLength = 16;

    /// <summary>The longest recovery code accepted; one is 19 characters as shown, more with spaces.</summary>
    internal const int MaxRecoveryCodeLength = 32;

    public StepUpCommandValidator(IOptions<PasswordOptions> passwordOptions)
    {
        var passwords = passwordOptions.Value;

        RuleFor(command => command)
            .Must(command => Count(command) == 1)
            .OverridePropertyName(ProofPropertyName)
            .WithMessage("Exactly one of 'password', 'code' and 'recoveryCode' is required.");

        RuleFor(command => command.Password)
            .MaximumLength(passwords.MaxLength);

        RuleFor(command => command.Code)
            .MaximumLength(MaxCodeLength);

        RuleFor(command => command.RecoveryCode)
            .MaximumLength(MaxRecoveryCodeLength);
    }

    private static int Count(StepUpCommand command) =>
        (string.IsNullOrWhiteSpace(command.Password) ? 0 : 1)
        + (string.IsNullOrWhiteSpace(command.Code) ? 0 : 1)
        + (string.IsNullOrWhiteSpace(command.RecoveryCode) ? 0 : 1);
}
