using TemplateName.Modules.Auth.Application.Authentication.Refresh;

namespace TemplateName.UnitTests.Auth;

public sealed class RefreshTokenCommandValidatorTests
{
    private readonly RefreshTokenCommandValidator _sut = new();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_token_is_rejected(string token)
    {
        _sut.Validate(new RefreshTokenCommand(token)).Errors.ShouldHaveSingleItem().PropertyName.ShouldBe(nameof(RefreshTokenCommand.RefreshToken));
    }

    [Fact]
    public void Token_longer_than_the_limit_is_rejected()
    {
        _sut.Validate(new RefreshTokenCommand(new string('a', RefreshTokenCommandValidator.MaxTokenLength))).IsValid.ShouldBeTrue();
        _sut.Validate(new RefreshTokenCommand(new string('a', RefreshTokenCommandValidator.MaxTokenLength + 1))).IsValid.ShouldBeFalse();
    }

    [Theory]
    [InlineData("not-a-token")]
    [InlineData("ÿ\u0000<script>")]
    public void Any_short_value_passes_so_the_handler_answers_401(string token)
    {
        _sut.Validate(new RefreshTokenCommand(token)).IsValid.ShouldBeTrue();
    }
}
