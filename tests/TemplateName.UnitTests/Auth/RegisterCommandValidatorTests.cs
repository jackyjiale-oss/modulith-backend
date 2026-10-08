using FluentValidation.TestHelper;
using Microsoft.Extensions.Options;
using TemplateName.Modules.Auth.Application.Passwords;
using TemplateName.Modules.Auth.Application.Registration.Register;

namespace TemplateName.UnitTests.Auth;

public sealed class RegisterCommandValidatorTests
{
    private readonly RegisterCommandValidator _sut = new(Options.Create(new PasswordOptions()));

    [Fact]
    public void Valid_command_passes()
    {
        var result = _sut.TestValidate(ValidCommand());

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(11, false)]
    [InlineData(12, true)]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public void Password_boundaries_are_enforced(int length, bool isAccepted)
    {
        var result = _sut.TestValidate(ValidCommand() with { Password = new string('p', length) });

        if (isAccepted)
        {
            result.ShouldNotHaveValidationErrorFor(command => command.Password);
        }
        else
        {
            result.ShouldHaveValidationErrorFor(command => command.Password);
        }
    }

    [Fact]
    public void Password_limits_come_from_the_options()
    {
        var sut = new RegisterCommandValidator(Options.Create(new PasswordOptions { MinLength = 16, MaxLength = 20 }));

        sut.TestValidate(ValidCommand() with { Password = new string('p', 15) }).ShouldHaveValidationErrorFor(command => command.Password);
        sut.TestValidate(ValidCommand() with { Password = new string('p', 16) }).ShouldNotHaveValidationErrorFor(command => command.Password);
        sut.TestValidate(ValidCommand() with { Password = new string('p', 21) }).ShouldHaveValidationErrorFor(command => command.Password);
    }

    [Fact]
    public void A_one_megabyte_password_is_rejected()
    {
        var result = _sut.TestValidate(ValidCommand() with { Password = new string('p', 1024 * 1024) });

        result.ShouldHaveValidationErrorFor(command => command.Password);
    }

    [Theory]
    [InlineData("alice.liddell@example.com")]
    [InlineData("ALICE.LIDDELL@EXAMPLE.COM")]
    [InlineData(" Alice.Liddell@Example.com ")]
    public void Password_equal_to_email_is_rejected(string password)
    {
        var result = _sut.TestValidate(ValidCommand() with { Email = "Alice.Liddell@example.com", Password = password });

        result.ShouldHaveValidationErrorFor(command => command.Password);
    }

    [Fact]
    public void Password_containing_the_email_but_longer_is_accepted()
    {
        var result = _sut.TestValidate(ValidCommand() with { Email = "alice.liddell@example.com", Password = "alice.liddell@example.com!" });

        result.ShouldNotHaveValidationErrorFor(command => command.Password);
    }

    [Theory]
    [InlineData("            ")]
    [InlineData("\t\t\t\t\t\t\t\t\t\t\t\t\t\t")]
    public void Whitespace_only_password_is_rejected(string password)
    {
        var result = _sut.TestValidate(ValidCommand() with { Password = password });

        result.ShouldHaveValidationErrorFor(command => command.Password);
    }

    [Theory]
    [InlineData("密码密码密码密码密码密码")]
    [InlineData("kata laluan rahsia ñü")]
    [InlineData("🔒🔒🔒🔒🔒🔒")]
    public void Unicode_password_is_accepted(string password)
    {
        var result = _sut.TestValidate(ValidCommand() with { Password = password });

        result.ShouldNotHaveValidationErrorFor(command => command.Password);
    }

    [Fact]
    public void Password_with_surrounding_spaces_is_kept_and_accepted()
    {
        var result = _sut.TestValidate(ValidCommand() with { Password = "  twelve chars  " });

        result.ShouldNotHaveValidationErrorFor(command => command.Password);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    [InlineData("@example.com")]
    [InlineData("alice@")]
    public void Invalid_email_is_rejected(string email)
    {
        var result = _sut.TestValidate(ValidCommand() with { Email = email });

        result.ShouldHaveValidationErrorFor(command => command.Email);
    }

    [Fact]
    public void Null_email_and_password_are_rejected_without_throwing()
    {
        var result = _sut.TestValidate(new RegisterCommand(null!, null!, null!, null!));

        result.ShouldHaveValidationErrorFor(command => command.Email);
        result.ShouldHaveValidationErrorFor(command => command.Password);
        result.ShouldHaveValidationErrorFor(command => command.DisplayName);
        result.ShouldHaveValidationErrorFor(command => command.Locale);
    }

    [Fact]
    public void Email_of_256_characters_passes_and_257_fails()
    {
        var domain = "@example.com";
        var email256 = new string('a', 256 - domain.Length) + domain;
        var email257 = new string('a', 257 - domain.Length) + domain;

        _sut.TestValidate(ValidCommand() with { Email = email256 }).ShouldNotHaveValidationErrorFor(command => command.Email);
        _sut.TestValidate(ValidCommand() with { Email = email257 }).ShouldHaveValidationErrorFor(command => command.Email);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("A", true)]
    public void Display_name_must_not_be_blank(string displayName, bool isAccepted)
    {
        var result = _sut.TestValidate(ValidCommand() with { DisplayName = displayName });

        if (isAccepted)
        {
            result.ShouldNotHaveValidationErrorFor(command => command.DisplayName);
        }
        else
        {
            result.ShouldHaveValidationErrorFor(command => command.DisplayName);
        }
    }

    [Fact]
    public void Display_name_of_200_characters_passes_and_201_fails()
    {
        _sut.TestValidate(ValidCommand() with { DisplayName = new string('d', 200) }).ShouldNotHaveValidationErrorFor(command => command.DisplayName);
        _sut.TestValidate(ValidCommand() with { DisplayName = new string('d', 201) }).ShouldHaveValidationErrorFor(command => command.DisplayName);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ms")]
    [InlineData("zh-Hans")]
    [InlineData("en-US")]
    [InlineData("ms-MY")]
    public void Predefined_culture_locale_is_accepted(string locale)
    {
        var result = _sut.TestValidate(ValidCommand() with { Locale = locale });

        result.ShouldNotHaveValidationErrorFor(command => command.Locale);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("xx-not-a-culture")]
    [InlineData("klingon")]
    [InlineData("en-US-x-private-17")]
    public void Unknown_or_long_locale_is_rejected(string locale)
    {
        var result = _sut.TestValidate(ValidCommand() with { Locale = locale });

        result.ShouldHaveValidationErrorFor(command => command.Locale);
    }

    private static RegisterCommand ValidCommand() => new("alice@example.com", "correct horse battery", "Alice", "en");
}
