using FluentValidation.TestHelper;
using NSubstitute;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.ExternalLogins.Login;

namespace TemplateName.UnitTests.Auth;

public sealed class ExternalLoginCommandValidatorTests
{
    private const string IdToken = "header.payload.signature";
    private const string Nonce = "nonce-0123456789abcdef";

    private readonly IExternalProviderCatalog _catalog = Substitute.For<IExternalProviderCatalog>();
    private readonly ExternalLoginCommandValidator _sut;

    public ExternalLoginCommandValidatorTests()
    {
        _catalog.Find(Arg.Is<string>(name => name != null && name.Trim().Equals("google", StringComparison.OrdinalIgnoreCase)))
            .Returns(Provider("google", ExternalProviderKind.Oidc));
        _catalog.Find(Arg.Is<string>(name => name != null && name.Trim().Equals("facebook", StringComparison.OrdinalIgnoreCase)))
            .Returns(Provider("facebook", ExternalProviderKind.Facebook));
        _sut = new ExternalLoginCommandValidator(_catalog);
    }

    [Fact]
    public void An_id_token_with_its_nonce_passes()
    {
        _sut.TestValidate(Command()).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void A_Facebook_access_token_passes_without_a_nonce()
    {
        _sut.TestValidate(Command("Facebook") with { IdToken = null, Nonce = null, AccessToken = "facebook-access-token" })
            .ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void A_Facebook_Limited_Login_id_token_passes()
    {
        _sut.TestValidate(Command("facebook")).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "  ")]
    [InlineData(IdToken, "facebook-access-token")]
    public void Exactly_one_of_the_id_token_and_the_access_token_is_required(string? idToken, string? accessToken)
    {
        var result = _sut.TestValidate(Command("facebook") with { IdToken = idToken, AccessToken = accessToken });

        result.ShouldHaveValidationErrorFor(ExternalLoginCommandValidator.ProofPropertyName);
    }

    [Fact]
    public void An_access_token_is_refused_for_a_provider_that_is_not_Facebook()
    {
        var result = _sut.TestValidate(Command() with { IdToken = null, Nonce = null, AccessToken = "access-token" });

        result.ShouldHaveValidationErrorFor(command => command.AccessToken);
    }

    [Theory]
    [InlineData("myspace")]
    [InlineData("apple")]
    public void An_unknown_or_disabled_provider_is_left_to_the_handler_whatever_the_proof(string provider)
    {
        // Find answers null for both, so the validator cannot tell them apart and the handler answers 404 for both.
        _sut.TestValidate(Command(provider) with { IdToken = null, Nonce = null, AccessToken = "access-token" }).ShouldNotHaveAnyValidationErrors();
        _sut.TestValidate(Command(provider)).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_nonce_is_required_with_an_id_token(string? nonce)
    {
        _sut.TestValidate(Command() with { Nonce = nonce }).ShouldHaveValidationErrorFor(command => command.Nonce);
    }

    [Theory]
    [InlineData(15, false)]
    [InlineData(16, true)]
    [InlineData(256, true)]
    [InlineData(257, false)]
    public void Nonce_is_16_to_256_characters(int length, bool isAccepted)
    {
        var result = _sut.TestValidate(Command() with { Nonce = new string('n', length) });

        if (isAccepted)
        {
            result.ShouldNotHaveValidationErrorFor(command => command.Nonce);
        }
        else
        {
            result.ShouldHaveValidationErrorFor(command => command.Nonce);
        }
    }

    [Theory]
    [InlineData(8192, true)]
    [InlineData(8193, false)]
    public void Tokens_are_at_most_8192_characters(int length, bool isAccepted)
    {
        var idToken = _sut.TestValidate(Command() with { IdToken = new string('t', length) });
        var accessToken = _sut.TestValidate(Command("facebook") with { IdToken = null, Nonce = null, AccessToken = new string('t', length) });

        if (isAccepted)
        {
            idToken.ShouldNotHaveAnyValidationErrors();
            accessToken.ShouldNotHaveAnyValidationErrors();
        }
        else
        {
            idToken.ShouldHaveValidationErrorFor(command => command.IdToken);
            accessToken.ShouldHaveValidationErrorFor(command => command.AccessToken);
        }
    }

    [Fact]
    public void A_one_megabyte_token_is_refused()
    {
        _sut.TestValidate(Command() with { IdToken = new string('t', 1024 * 1024) }).ShouldHaveValidationErrorFor(command => command.IdToken);
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(201, false)]
    public void Display_name_and_device_name_are_at_most_200_characters(int length, bool isAccepted)
    {
        var result = _sut.TestValidate(Command() with { DisplayName = new string('d', length), DeviceName = new string('d', length) });

        if (isAccepted)
        {
            result.ShouldNotHaveAnyValidationErrors();
        }
        else
        {
            result.ShouldHaveValidationErrorFor(command => command.DisplayName);
            result.ShouldHaveValidationErrorFor(command => command.DeviceName);
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ms")]
    [InlineData("zh-Hans")]
    [InlineData("ZH-hans")]
    public void A_predefined_culture_is_accepted_as_locale(string locale)
    {
        _sut.TestValidate(Command() with { Locale = locale }).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("xx-NOPE")]
    [InlineData("not a culture")]
    [InlineData("en-US-x-a-very-long-private-use-tag")]
    public void Anything_else_is_refused_as_locale(string locale)
    {
        _sut.TestValidate(Command() with { Locale = locale }).ShouldHaveValidationErrorFor(command => command.Locale);
    }

    private static ExternalLoginCommand Command(string provider = "google") => new(provider, IdToken, null, Nonce, null, null, null);

    private static ExternalProvider Provider(string name, ExternalProviderKind kind) => new(
        name,
        name,
        kind,
        Authority: null,
        Issuers: [],
        ClientIds: ["client"],
        ExternalEmailRule.EmailVerifiedClaim,
        TrustEmail: false,
        TenantId: null,
        AppId: null,
        AppSecret: null);
}
