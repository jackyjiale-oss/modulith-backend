using FluentValidation;

namespace TemplateName.Modules.Auth.Application.Authentication.Refresh;

/// <summary>
/// Refuses only a blank or absurdly long token, from the request alone. Any other value reaches the handler, which answers an unknown
/// token with the same 401 as one whose session has ended.
/// </summary>
internal sealed class RefreshTokenCommandValidator : AbstractValidator<RefreshTokenCommand>
{
    /// <summary>A real token is 43 characters; the limit only keeps an absurd value away from hashing.</summary>
    internal const int MaxTokenLength = 256;

    public RefreshTokenCommandValidator()
    {
        RuleFor(command => command.RefreshToken)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MaximumLength(MaxTokenLength);
    }
}
