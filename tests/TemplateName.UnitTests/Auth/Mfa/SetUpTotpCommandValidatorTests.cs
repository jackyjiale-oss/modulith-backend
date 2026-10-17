using FluentValidation.TestHelper;
using TemplateName.Modules.Auth.Application.Mfa.SetUpTotp;

namespace TemplateName.UnitTests.Auth.Mfa;

public sealed class SetUpTotpCommandValidatorTests
{
    private readonly SetUpTotpCommandValidator _sut = new();

    [Theory]
    [InlineData(null)]
    [InlineData("Phone")]
    [InlineData(" Work phone ")]
    public void Missing_or_named_label_is_accepted(string? label)
    {
        _sut.TestValidate(new SetUpTotpCommand(label)).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Blank_label_is_refused(string label)
    {
        _sut.TestValidate(new SetUpTotpCommand(label)).ShouldHaveValidationErrorFor(command => command.Label);
    }

    [Theory]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public void Label_is_at_most_one_hundred_characters(int length, bool isAccepted)
    {
        var result = _sut.TestValidate(new SetUpTotpCommand(new string('l', length)));

        if (isAccepted)
        {
            result.ShouldNotHaveAnyValidationErrors();
        }
        else
        {
            result.ShouldHaveValidationErrorFor(command => command.Label);
        }
    }
}
