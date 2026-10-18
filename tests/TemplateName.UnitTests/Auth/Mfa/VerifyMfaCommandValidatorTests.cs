using FluentValidation.TestHelper;
using TemplateName.Modules.Auth.Application.Mfa.Verify;

namespace TemplateName.UnitTests.Auth.Mfa;

public sealed class VerifyMfaCommandValidatorTests
{
    private const string Token = "challenge-token-value-of-forty-three-chars";
    private const string Code = "123456";
    private const string RecoveryCode = "ABCD-EFGH-JKMN-PQRS";

    private readonly VerifyMfaCommandValidator _sut = new();

    [Theory]
    [InlineData(Code, null)]
    [InlineData(null, RecoveryCode)]
    [InlineData("", RecoveryCode)]
    [InlineData(Code, "  ")]
    [InlineData(null, "abcd efgh jkmn pqrs\n")]
    public void Token_with_exactly_one_proof_is_accepted(string? code, string? recoveryCode)
    {
        _sut.TestValidate(new VerifyMfaCommand(Token, code, recoveryCode, RememberDevice: true)).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", " ")]
    [InlineData(Code, RecoveryCode)]
    public void No_proof_or_both_is_refused(string? code, string? recoveryCode)
    {
        var result = _sut.TestValidate(new VerifyMfaCommand(Token, code, recoveryCode, RememberDevice: false));

        result.ShouldHaveValidationErrorFor(VerifyMfaCommandValidator.ProofPropertyName)
            .WithErrorMessage("Exactly one of 'code' and 'recoveryCode' is required.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_challenge_token_is_refused(string? token)
    {
        _sut.TestValidate(new VerifyMfaCommand(token!, Code, null, RememberDevice: false)).ShouldHaveValidationErrorFor(command => command.ChallengeToken);
    }

    [Theory]
    [InlineData(256, true)]
    [InlineData(257, false)]
    [InlineData(1024 * 1024, false)]
    public void Challenge_token_is_at_most_256_characters(int length, bool isAccepted)
    {
        var result = _sut.TestValidate(new VerifyMfaCommand(new string('t', length), Code, null, RememberDevice: false));

        if (isAccepted)
        {
            result.ShouldNotHaveAnyValidationErrors();
        }
        else
        {
            result.ShouldHaveValidationErrorFor(command => command.ChallengeToken);
        }
    }

    [Theory]
    [InlineData("000000")]
    [InlineData("987654")]
    public void Six_ascii_digits_are_a_code(string code)
    {
        _sut.TestValidate(new VerifyMfaCommand(Token, code, null, RememberDevice: false)).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12345a")]
    [InlineData(" 123456")]
    [InlineData("123 456")]
    [InlineData("１２３４５６")]
    [InlineData("١٢٣٤٥٦")]
    public void Anything_but_six_ascii_digits_is_refused_as_a_code(string code)
    {
        _sut.TestValidate(new VerifyMfaCommand(Token, code, null, RememberDevice: false)).ShouldHaveValidationErrorFor(command => command.Code);
    }

    [Theory]
    [InlineData(32, true)]
    [InlineData(33, false)]
    public void Recovery_code_is_at_most_32_characters(int length, bool isAccepted)
    {
        var result = _sut.TestValidate(new VerifyMfaCommand(Token, null, new string('A', length), RememberDevice: false));

        if (isAccepted)
        {
            result.ShouldNotHaveAnyValidationErrors();
        }
        else
        {
            result.ShouldHaveValidationErrorFor(command => command.RecoveryCode);
        }
    }
}
