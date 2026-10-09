using FluentValidation.TestHelper;
using TemplateName.Modules.Auth.Application.Registration.ConfirmEmail;

namespace TemplateName.UnitTests.Auth;

public sealed class ConfirmEmailCommandValidatorTests
{
    private readonly ConfirmEmailCommandValidator _sut = new();

    [Theory]
    [InlineData(1)]
    [InlineData(43)]
    [InlineData(256)]
    public void Token_up_to_256_characters_passes(int length)
    {
        _sut.TestValidate(new ConfirmEmailCommand(new string('t', length))).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Blank_token_fails(string? token)
    {
        _sut.TestValidate(new ConfirmEmailCommand(token!)).ShouldHaveValidationErrorFor(command => command.Token);
    }

    [Fact]
    public void Token_of_257_characters_fails()
    {
        _sut.TestValidate(new ConfirmEmailCommand(new string('t', 257))).ShouldHaveValidationErrorFor(command => command.Token);
    }
}
