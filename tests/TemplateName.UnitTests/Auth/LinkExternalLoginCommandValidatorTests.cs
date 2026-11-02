using FluentValidation.TestHelper;
using NSubstitute;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.ExternalLogins.Link;
using TemplateName.Modules.Auth.Application.ExternalLogins.Login;

namespace TemplateName.UnitTests.Auth;

public sealed class LinkExternalLoginCommandValidatorTests
{
    private const string IdToken = "header.payload.signature";
    private const string Nonce = "nonce-0123456789abcdef";

    private readonly IExternalProviderCatalog _catalog = Substitute.For<IExternalProviderCatalog>();
    private readonly LinkExternalLoginCommandValidator _sut;

    public LinkExternalLoginCommandValidatorTests()
    {
        _catalog.Find(Arg.Is<string>(name => name != null && name.Trim().Equals("google", StringComparison.OrdinalIgnoreCase)))
            .Returns(Provider("google", ExternalProviderKind.Oidc));
        _catalog.Find(Arg.Is<string>(name => name != null && name.Trim().Equals("facebook", StringComparison.OrdinalIgnoreCase)))
            .Returns(Provider("facebook", ExternalProviderKind.Facebook));
        _sut = new LinkExternalLoginCommandValidator(_catalog);
    }

    [Fact]
    public void An_id_token_with_its_nonce_passes()
    {
        _sut.TestValidate(Command()).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void A_Facebook_access_token_passes_without_a_nonce()
    {
        _sut.TestValidate(new LinkExternalLoginCommand("Facebook", null, "facebook-access-token", null)).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "  ")]
    [InlineData(IdToken, "facebook-access-token")]
    public void Exactly_one_of_the_id_token_and_the_access_token_is_required(string? idToken, string? accessToken)
    {
        var result = _sut.TestValidate(new LinkExternalLoginCommand("facebook", idToken, accessToken, Nonce));

        result.ShouldHaveValidationErrorFor(ExternalLoginCommandValidator.ProofPropertyName);
    }

    [Fact]
    public void An_access_token_is_refused_for_a_provider_that_is_not_Facebook()
    {
        var result = _sut.TestValidate(new LinkExternalLoginCommand("google", null, "access-token", null));

        result.ShouldHaveValidationErrorFor(command => command.AccessToken);
    }

    [Theory]
    [InlineData("myspace")]
    [InlineData("apple")]
    public void An_unknown_or_disabled_provider_is_left_to_the_handler_whatever_the_proof(string provider)
    {
        _sut.TestValidate(new LinkExternalLoginCommand(provider, null, "access-token", null)).ShouldNotHaveAnyValidationErrors();
        _sut.TestValidate(new LinkExternalLoginCommand(provider, IdToken, null, Nonce)).ShouldNotHaveAnyValidationErrors();
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
        var accessToken = _sut.TestValidate(new LinkExternalLoginCommand("facebook", null, new string('t', length), null));

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

    private static LinkExternalLoginCommand Command() => new("google", IdToken, null, Nonce);

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
