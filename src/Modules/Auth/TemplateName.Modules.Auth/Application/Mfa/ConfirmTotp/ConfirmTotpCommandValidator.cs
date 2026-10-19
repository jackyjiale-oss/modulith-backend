using FluentValidation;
using TemplateName.Modules.Auth.Domain.Mfa;

namespace TemplateName.Modules.Auth.Application.Mfa.ConfirmTotp;

/// <summary>
/// Runs in the validation decorator, before the handler: the code is exactly <see cref="Totp.Digits"/> ASCII digits (no spaces, no
/// full-width or other scripts' digits), the form an authenticator app shows. A confirm counts no failure, so refusing a malformed code
/// here costs nothing and spares the handler a lookup.
/// </summary>
internal sealed class ConfirmTotpCommandValidator : AbstractValidator<ConfirmTotpCommand>
{
    public ConfirmTotpCommandValidator()
    {
        RuleFor(command => command.Code)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Must(IsSixAsciiDigits)
            .WithMessage($"'code' must be exactly {Totp.Digits} digits.");
    }

    private static bool IsSixAsciiDigits(string code) => code.Length == Totp.Digits && code.All(char.IsAsciiDigit);
}
