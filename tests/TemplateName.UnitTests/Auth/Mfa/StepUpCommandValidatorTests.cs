using FluentValidation.TestHelper;
using Microsoft.Extensions.Options;
using TemplateName.Modules.Auth.Application.Passwords;
using TemplateName.Modules.Auth.Application.StepUp;

namespace TemplateName.UnitTests.Auth.Mfa;

public sealed class StepUpCommandValidatorTests
{
    private const string Password = "correct horse battery";
    private const string Code = "123456";
    private const string RecoveryCode = "ABCD-EFGH-JKMN-PQRS";

    private readonly StepUpCommandValidator _sut = new(Options.Create(new PasswordOptions()));

    [Theory]
    [InlineData(Password, null, null)]
    [InlineData(null, Code, null)]
    [InlineData(null, null, RecoveryCode)]
    [InlineData("", Code, null)]
    [InlineData(null, "  ", RecoveryCode)]
    [InlineData(" ", null, RecoveryCode)]
    public void Exactly_one_proof_is_accepted(string? password, string? code, string? recoveryCode)
    {
        _sut.TestValidate(new StepUpCommand(password, code, recoveryCode)).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", " ", "\t")]
    [InlineData(Password, Code, null)]
    [InlineData(Password, null, RecoveryCode)]
    [InlineData(null, Code, RecoveryCode)]
    [InlineData(Password, Code, RecoveryCode)]
    public void No_proof_or_more_than_one_is_refused(string? password, string? code, string? recoveryCode)
    {
        var result = _sut.TestValidate(new StepUpCommand(password, code, recoveryCode));

        result.ShouldHaveValidationErrorFor(StepUpCommandValidator.ProofPropertyName)
            .WithErrorMessage("Exactly one of 'password', 'code' and 'recoveryCode' is required.");
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(128, true)]
    [InlineData(129, false)]
    [InlineData(1024 * 1024, false)]
    public void Password_is_at_most_the_maximum_length(int length, bool isAccepted)
    {
        var result = _sut.TestValidate(new StepUpCommand(new string('p', length), null, null));

        if (isAccepted)
        {
            result.ShouldNotHaveAnyValidationErrors();
        }
        else
        {
            result.ShouldHaveValidationErrorFor(command => command.Password);
        }
    }

    [Fact]
    public void Password_limit_follows_the_options()
    {
        var validator = new StepUpCommandValidator(Options.Create(new PasswordOptions { MinLength = 8, MaxLength = 20 }));

        validator.TestValidate(new StepUpCommand(new string('p', 20), null, null)).ShouldNotHaveAnyValidationErrors();
        validator.TestValidate(new StepUpCommand(new string('p', 21), null, null)).ShouldHaveValidationErrorFor(command => command.Password);
    }

    [Theory]
    [InlineData(16, true)]
    [InlineData(17, false)]
    public void Code_is_at_most_16_characters(int length, bool isAccepted)
    {
        var result = _sut.TestValidate(new StepUpCommand(null, new string('1', length), null));

        if (isAccepted)
        {
            result.ShouldNotHaveAnyValidationErrors();
        }
        else
        {
            result.ShouldHaveValidationErrorFor(command => command.Code);
        }
    }

    [Theory]
    [InlineData(32, true)]
    [InlineData(33, false)]
    public void Recovery_code_is_at_most_32_characters(int length, bool isAccepted)
    {
        var result = _sut.TestValidate(new StepUpCommand(null, null, new string('A', length)));

        if (isAccepted)
        {
            result.ShouldNotHaveAnyValidationErrors();
        }
        else
        {
            result.ShouldHaveValidationErrorFor(command => command.RecoveryCode);
        }
    }

    [Fact]
    public void Command_text_never_shows_a_proof()
    {
        var text = new StepUpCommand(Password, Code, RecoveryCode).ToString();

        text.ShouldBe(nameof(StepUpCommand));
    }
}
