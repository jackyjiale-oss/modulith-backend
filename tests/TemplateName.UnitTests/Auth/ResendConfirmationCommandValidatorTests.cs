using FluentValidation.TestHelper;
using TemplateName.Modules.Auth.Application.Registration.ResendConfirmation;

namespace TemplateName.UnitTests.Auth;

public sealed class ResendConfirmationCommandValidatorTests
{
    private readonly ResendConfirmationCommandValidator _sut = new();

    [Theory]
    [InlineData("alice@example.com", true)]
    [InlineData(" Alice@Example.com ", true)]
    [InlineData("", false)]
    [InlineData("not-an-email", false)]
    [InlineData(null, false)]
    public void Email_is_validated(string? email, bool isAccepted)
    {
        var result = _sut.TestValidate(new ResendConfirmationCommand(email!));

        if (isAccepted)
        {
            result.ShouldNotHaveAnyValidationErrors();
        }
        else
        {
            result.ShouldHaveValidationErrorFor(command => command.Email);
        }
    }

    [Fact]
    public void Email_longer_than_256_characters_fails()
    {
        var email = new string('a', 245) + "@example.com";

        _sut.TestValidate(new ResendConfirmationCommand(email)).ShouldHaveValidationErrorFor(command => command.Email);
    }
}
