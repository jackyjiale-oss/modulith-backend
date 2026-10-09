using FluentValidation.TestHelper;
using Microsoft.Extensions.Options;
using TemplateName.Modules.Auth.Application.Passwords;
using TemplateName.Modules.Auth.Application.Passwords.Change;
using TemplateName.Modules.Auth.Application.Passwords.Forgot;
using TemplateName.Modules.Auth.Application.Passwords.Reset;

namespace TemplateName.UnitTests.Auth;

/// <summary>The validators of forgot, reset and change: the new-password limits are those of registration (Task 13, Ruling R14).</summary>
public sealed class PasswordCommandValidatorTests
{
    private const string Token = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Password = "correct horse battery staple";

    private readonly ForgotPasswordCommandValidator _forgot = new();
    private readonly ResetPasswordCommandValidator _reset = new(Options.Create(new PasswordOptions()));
    private readonly ChangePasswordCommandValidator _change = new(Options.Create(new PasswordOptions()));

    [Fact]
    public void Valid_commands_pass()
    {
        _forgot.TestValidate(new ForgotPasswordCommand("alice@example.com")).ShouldNotHaveAnyValidationErrors();
        _reset.TestValidate(new ResetPasswordCommand(Token, Password)).ShouldNotHaveAnyValidationErrors();
        _change.TestValidate(new ChangePasswordCommand(Password, "another long passphrase")).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(11, false)]
    [InlineData(12, true)]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public void New_password_boundaries_are_enforced(int length, bool isAccepted)
    {
        var newPassword = new string('p', length);
        var reset = _reset.TestValidate(new ResetPasswordCommand(Token, newPassword));
        var change = _change.TestValidate(new ChangePasswordCommand(Password, newPassword));

        if (isAccepted)
        {
            reset.ShouldNotHaveValidationErrorFor(command => command.NewPassword);
            change.ShouldNotHaveValidationErrorFor(command => command.NewPassword);
        }
        else
        {
            reset.ShouldHaveValidationErrorFor(command => command.NewPassword);
            change.ShouldHaveValidationErrorFor(command => command.NewPassword);
        }
    }

    [Fact]
    public void New_password_limits_come_from_the_options()
    {
        var options = Options.Create(new PasswordOptions { MinLength = 16, MaxLength = 20 });
        var reset = new ResetPasswordCommandValidator(options);
        var change = new ChangePasswordCommandValidator(options);

        foreach (var (length, isAccepted) in new[] { (15, false), (16, true), (20, true), (21, false) })
        {
            var newPassword = new string('p', length);
            reset.TestValidate(new ResetPasswordCommand(Token, newPassword)).IsValid.ShouldBe(isAccepted);
            change.TestValidate(new ChangePasswordCommand("current", newPassword)).IsValid.ShouldBe(isAccepted);
        }
    }

    [Fact]
    public void A_one_megabyte_password_is_rejected()
    {
        var huge = new string('p', 1024 * 1024);

        _reset.TestValidate(new ResetPasswordCommand(Token, huge)).ShouldHaveValidationErrorFor(command => command.NewPassword);
        _change.TestValidate(new ChangePasswordCommand(Password, huge)).ShouldHaveValidationErrorFor(command => command.NewPassword);
        _change.TestValidate(new ChangePasswordCommand(huge, Password)).ShouldHaveValidationErrorFor(command => command.CurrentPassword);
    }

    [Theory]
    [InlineData("")]
    [InlineData("              ")]
    public void Empty_or_whitespace_only_new_password_is_rejected(string newPassword)
    {
        _reset.TestValidate(new ResetPasswordCommand(Token, newPassword)).ShouldHaveValidationErrorFor(command => command.NewPassword);
        _change.TestValidate(new ChangePasswordCommand(Password, newPassword)).ShouldHaveValidationErrorFor(command => command.NewPassword);
    }

    [Theory]
    [InlineData("密码密码密码密码密码密码")]
    [InlineData("pässwörd-mit-ümläüten")]
    [InlineData("🔐🔐🔐🔐🔐🔐")]
    public void Unicode_new_password_is_accepted(string newPassword)
    {
        _reset.TestValidate(new ResetPasswordCommand(Token, newPassword)).ShouldNotHaveValidationErrorFor(command => command.NewPassword);
        _change.TestValidate(new ChangePasswordCommand(Password, newPassword)).ShouldNotHaveValidationErrorFor(command => command.NewPassword);
    }

    [Fact]
    public void Current_password_has_no_minimum_but_is_required()
    {
        // A password an older, shorter minimum accepted must still prove the caller, as at login.
        _change.TestValidate(new ChangePasswordCommand("short", Password)).ShouldNotHaveValidationErrorFor(command => command.CurrentPassword);
        _change.TestValidate(new ChangePasswordCommand("", Password)).ShouldHaveValidationErrorFor(command => command.CurrentPassword);
        _change.TestValidate(new ChangePasswordCommand(new string('p', 128), Password)).ShouldNotHaveValidationErrorFor(command => command.CurrentPassword);
        _change.TestValidate(new ChangePasswordCommand(new string('p', 129), Password)).ShouldHaveValidationErrorFor(command => command.CurrentPassword);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Reset_token_is_required(string token)
    {
        _reset.TestValidate(new ResetPasswordCommand(token, Password)).ShouldHaveValidationErrorFor(command => command.Token);
    }

    [Fact]
    public void Reset_token_longer_than_256_characters_is_rejected()
    {
        _reset.TestValidate(new ResetPasswordCommand(new string('t', 256), Password)).ShouldNotHaveValidationErrorFor(command => command.Token);
        _reset.TestValidate(new ResetPasswordCommand(new string('t', 257), Password)).ShouldHaveValidationErrorFor(command => command.Token);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    public void Forgot_email_must_be_an_address(string email)
    {
        _forgot.TestValidate(new ForgotPasswordCommand(email)).ShouldHaveValidationErrorFor(command => command.Email);
    }

    [Fact]
    public void Forgot_email_longer_than_256_characters_is_rejected()
    {
        var email = new string('a', 256 - "@example.com".Length + 1) + "@example.com";

        _forgot.TestValidate(new ForgotPasswordCommand(email)).ShouldHaveValidationErrorFor(command => command.Email);
    }
}
