using FluentValidation.TestHelper;
using Microsoft.Extensions.Options;
using TemplateName.Modules.Auth.Application.Authentication.Login;
using TemplateName.Modules.Auth.Application.Passwords;

namespace TemplateName.UnitTests.Auth;

public sealed class LoginCommandValidatorTests
{
    private const string Password = "correct horse battery";

    private readonly LoginCommandValidator _sut = new(Options.Create(new PasswordOptions()));

    [Theory]
    [InlineData("alice@example.com", true)]
    [InlineData(" Alice@Example.com ", true)]
    [InlineData("", false)]
    [InlineData("not-an-email", false)]
    [InlineData(null, false)]
    public void Email_is_validated(string? email, bool isAccepted)
    {
        var result = _sut.TestValidate(new LoginCommand(email!, Password, null));

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

        _sut.TestValidate(new LoginCommand(email, Password, null)).ShouldHaveValidationErrorFor(command => command.Email);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(11)]
    [InlineData(128)]
    public void Any_password_up_to_the_maximum_is_accepted_without_the_minimum_length(int length)
    {
        // A password shorter than today's minimum may still be the one an older policy accepted; only the hash can tell.
        _sut.TestValidate(new LoginCommand("alice@example.com", new string('p', length), null)).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(129)]
    [InlineData(1024 * 1024)]
    public void Password_longer_than_the_maximum_fails(int length)
    {
        _sut.TestValidate(new LoginCommand("alice@example.com", new string('p', length), null)).ShouldHaveValidationErrorFor(command => command.Password);
    }

    [Fact]
    public void Maximum_comes_from_the_password_options()
    {
        var validator = new LoginCommandValidator(Options.Create(new PasswordOptions { MaxLength = 64 }));

        validator.TestValidate(new LoginCommand("alice@example.com", new string('p', 65), null)).ShouldHaveValidationErrorFor(command => command.Password);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Empty_password_fails(string? password)
    {
        _sut.TestValidate(new LoginCommand("alice@example.com", password!, null)).ShouldHaveValidationErrorFor(command => command.Password);
    }

    [Fact]
    public void Device_name_is_optional_and_at_most_200_characters()
    {
        _sut.TestValidate(new LoginCommand("alice@example.com", Password, null)).ShouldNotHaveAnyValidationErrors();
        _sut.TestValidate(new LoginCommand("alice@example.com", Password, new string('d', 200))).ShouldNotHaveAnyValidationErrors();
        _sut.TestValidate(new LoginCommand("alice@example.com", Password, new string('d', 201))).ShouldHaveValidationErrorFor(command => command.DeviceName);
    }
}
