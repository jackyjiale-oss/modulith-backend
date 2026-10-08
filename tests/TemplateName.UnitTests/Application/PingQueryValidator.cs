using FluentValidation;

namespace TemplateName.UnitTests.Application;

internal sealed class PingQueryValidator : AbstractValidator<PingQuery>
{
    public PingQueryValidator() => RuleFor(query => query.Reason).NotEmpty();
}
