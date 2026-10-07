using FluentValidation;

namespace TemplateName.UnitTests.Application;

internal sealed class PingNoResponseValidator : AbstractValidator<PingNoResponseCommand>
{
    public PingNoResponseValidator() => RuleFor(command => command.Reason).NotEmpty();
}
