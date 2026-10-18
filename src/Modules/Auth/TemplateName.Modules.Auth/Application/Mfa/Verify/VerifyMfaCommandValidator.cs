using FluentValidation;
using TemplateName.Modules.Auth.Domain.Mfa;

namespace TemplateName.Modules.Auth.Application.Mfa.Verify;

/// <summary>
/// Runs in the validation decorator, before the handler, so a request that cannot be a verification is refused before any lookup: the
/// challenge token not blank and at most <see cref="MaxChallengeTokenLength"/> characters (a real one is 43), and exactly one of a code
/// (exactly <see cref="Totp.Digits"/> ASCII digits, the form an authenticator app shows) and a recovery code (at most
/// <see cref="MaxRecoveryCodeLength"/> characters; its spelling is the verifier's business). A blank value counts as absent. The answer
/// depends on the request alone, so a refused request counts nothing and says nothing about a challenge.
/// </summary>
internal sealed class VerifyMfaCommandValidator : AbstractValidator<VerifyMfaCommand>
{
    /// <summary>The key of the "exactly one proof" error in the validation problem's <c>errors</c>.</summary>
    internal const string ProofPropertyName = "proof";

    /// <summary>The longest challenge token accepted (as for a refresh token); a real one has 43 characters.</summary>
    internal const int MaxChallengeTokenLength = 256;

    /// <summary>The longest recovery code accepted; one is 19 characters as shown, more with spaces.</summary>
    internal const int MaxRecoveryCodeLength = 32;

    public VerifyMfaCommandValidator()
    {
        RuleFor(command => command.ChallengeToken)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MaximumLength(MaxChallengeTokenLength);

        RuleFor(command => command)
            .Must(command => IsGiven(command.Code) != IsGiven(command.RecoveryCode))
            .OverridePropertyName(ProofPropertyName)
            .WithMessage("Exactly one of 'code' and 'recoveryCode' is required.");

        RuleFor(command => command.Code)
            .Must(code => code!.Length == Totp.Digits && code.All(char.IsAsciiDigit))
            .When(command => IsGiven(command.Code))
            .WithMessage($"'code' must be exactly {Totp.Digits} digits.");

        RuleFor(command => command.RecoveryCode)
            .MaximumLength(MaxRecoveryCodeLength);
    }

    private static bool IsGiven(string? value) => !string.IsNullOrWhiteSpace(value);
}
