using FluentValidation.TestHelper;
using TemplateName.Modules.Auth.Application.Mfa.ConfirmTotp;

namespace TemplateName.UnitTests.Auth.Mfa;

public sealed class ConfirmTotpCommandValidatorTests
{
    private readonly ConfirmTotpCommandValidator _sut = new();

    [Theory]
    [InlineData("123456")]
    [InlineData("000000")]
    public void Six_digits_are_accepted(string code)
    {
        _sut.TestValidate(new ConfirmTotpCommand(code)).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("      ")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12345a")]
    [InlineData(" 123456")]
    [InlineData("123 456")]
    [InlineData("１２３４５６")]
    [InlineData("١٢٣٤٥٦")]
    public void Anything_but_six_ascii_digits_is_refused(string? code)
    {
        _sut.TestValidate(new ConfirmTotpCommand(code!)).ShouldHaveValidationErrorFor(command => command.Code);
    }
}
