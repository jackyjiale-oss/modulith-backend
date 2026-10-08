using FluentValidation;

namespace TemplateName.UnitTests.Application;

internal sealed class PingValidator : AbstractValidator<PingCommand>
{
    public PingValidator() => RuleFor(command => command.Reason).NotEmpty();
}
