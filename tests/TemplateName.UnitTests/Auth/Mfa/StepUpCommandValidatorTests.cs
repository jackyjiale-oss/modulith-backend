using FluentValidation.TestHelper;
using Microsoft.Extensions.Options;
using NSubstitute;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Passwords;
using TemplateName.Modules.Auth.Application.StepUp;

namespace TemplateName.UnitTests.Auth.Mfa;

public sealed class StepUpCommandValidatorTests
{
    private const string Password = "correct horse battery";
    private const string Code = "123456";
    private const string RecoveryCode = "ABCD-EFGH-JKMN-PQRS";

    private const string IdToken = "header.payload.signature";
    private const string Nonce = "nonce-0123456789abcdef";
    private const string ExactlyOneMessage = "Exactly one of 'password', 'code', 'recoveryCode' and a provider proof ('provider' with its token) is required.";

    private readonly IExternalProviderCatalog _catalog = Substitute.For<IExternalProviderCatalog>();
    private readonly StepUpCommandValidator _sut;

    public StepUpCommandValidatorTests()
    {
        _catalog.Find(Arg.Is<string>(name => name != null && name.Trim().Equals("google", StringComparison.OrdinalIgnoreCase)))
            .Returns(Provider("google", ExternalProviderKind.Oidc));
        _catalog.Find(Arg.Is<string>(name => name != null && name.Trim().Equals("facebook", StringComparison.OrdinalIgnoreCase)))
            .Returns(Provider("facebook", ExternalProviderKind.Facebook));
        _sut = new StepUpCommandValidator(Options.Create(new PasswordOptions()), _catalog);
    }

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
            .WithErrorMessage(ExactlyOneMessage);
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
        var validator = new StepUpCommandValidator(Options.Create(new PasswordOptions { MinLength = 8, MaxLength = 20 }), _catalog);

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

    [Fact]
    public void An_id_token_with_its_nonce_and_a_provider_is_one_proof()
    {
        _sut.TestValidate(Proof()).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void A_blank_password_and_codes_do_not_count_next_to_a_provider_proof()
    {
        _sut.TestValidate(Proof() with { Password = " ", Code = string.Empty, RecoveryCode = "\t" }).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void A_Facebook_access_token_passes_without_a_nonce()
    {
        _sut.TestValidate(Proof("Facebook") with { IdToken = null, Nonce = null, AccessToken = "facebook-access-token" })
            .ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void A_Facebook_Limited_Login_id_token_passes()
    {
        _sut.TestValidate(Proof("facebook")).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(Password, null, null)]
    [InlineData(null, Code, null)]
    [InlineData(null, null, RecoveryCode)]
    [InlineData(Password, Code, RecoveryCode)]
    public void A_provider_proof_together_with_another_proof_is_refused(string? password, string? code, string? recoveryCode)
    {
        var result = _sut.TestValidate(Proof() with { Password = password, Code = code, RecoveryCode = recoveryCode });

        result.ShouldHaveValidationErrorFor(StepUpCommandValidator.ProofPropertyName).WithErrorMessage(ExactlyOneMessage);
    }

    [Theory]
    [InlineData(null, null, "nonce-0123456789abcdef")]
    [InlineData(null, "header.payload.signature", null)]
    [InlineData(null, "header.payload.signature", "nonce-0123456789abcdef")]
    public void A_member_of_the_provider_proof_without_the_provider_is_refused(string? provider, string? idToken, string? nonce)
    {
        // A stray token or nonce is an incomplete proof, never silently ignored.
        var result = _sut.TestValidate(new StepUpCommand(null, null, null, provider, idToken, null, nonce));

        result.ShouldHaveValidationErrorFor(command => command.Provider);
    }

    [Fact]
    public void A_stray_nonce_next_to_a_password_is_more_than_one_proof()
    {
        var result = _sut.TestValidate(new StepUpCommand(Password, null, null, Nonce: Nonce));

        result.ShouldHaveValidationErrorFor(StepUpCommandValidator.ProofPropertyName).WithErrorMessage(ExactlyOneMessage);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "  ")]
    [InlineData(IdToken, "facebook-access-token")]
    public void Exactly_one_of_the_id_token_and_the_access_token_is_required(string? idToken, string? accessToken)
    {
        var result = _sut.TestValidate(Proof("facebook") with { IdToken = idToken, AccessToken = accessToken });

        result.ShouldHaveValidationErrorFor(StepUpCommandValidator.ProofPropertyName)
            .WithErrorMessage("Exactly one of 'idToken' and 'accessToken' is required.");
    }

    [Fact]
    public void An_access_token_is_refused_for_a_provider_that_is_not_Facebook()
    {
        var result = _sut.TestValidate(Proof() with { IdToken = null, Nonce = null, AccessToken = "access-token" });

        result.ShouldHaveValidationErrorFor(command => command.AccessToken);
    }

    [Fact]
    public void A_nonce_is_required_with_an_id_token()
    {
        _sut.TestValidate(Proof() with { Nonce = null }).ShouldHaveValidationErrorFor(command => command.Nonce);
        _sut.TestValidate(Proof() with { Nonce = "  " }).ShouldHaveValidationErrorFor(command => command.Nonce);
    }

    [Theory]
    [InlineData(15, false)]
    [InlineData(16, true)]
    [InlineData(256, true)]
    [InlineData(257, false)]
    public void The_nonce_has_16_to_256_characters(int length, bool isAccepted)
    {
        var result = _sut.TestValidate(Proof() with { Nonce = new string('n', length) });

        if (isAccepted)
        {
            result.ShouldNotHaveAnyValidationErrors();
        }
        else
        {
            result.ShouldHaveValidationErrorFor(command => command.Nonce);
        }
    }

    [Theory]
    [InlineData(8192, true)]
    [InlineData(8193, false)]
    [InlineData(1024 * 1024, false)]
    public void The_id_token_is_at_most_8192_characters(int length, bool isAccepted)
    {
        var result = _sut.TestValidate(Proof() with { IdToken = new string('t', length) });

        if (isAccepted)
        {
            result.ShouldNotHaveAnyValidationErrors();
        }
        else
        {
            result.ShouldHaveValidationErrorFor(command => command.IdToken);
        }
    }

    [Theory]
    [InlineData(8192, true)]
    [InlineData(8193, false)]
    public void The_access_token_is_at_most_8192_characters(int length, bool isAccepted)
    {
        var result = _sut.TestValidate(Proof("facebook") with { IdToken = null, Nonce = null, AccessToken = new string('t', length) });

        if (isAccepted)
        {
            result.ShouldNotHaveAnyValidationErrors();
        }
        else
        {
            result.ShouldHaveValidationErrorFor(command => command.AccessToken);
        }
    }

    [Theory]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public void The_provider_is_at_most_64_characters(int length, bool isAccepted)
    {
        var result = _sut.TestValidate(Proof(new string('p', length)));

        if (isAccepted)
        {
            result.ShouldNotHaveAnyValidationErrors();
        }
        else
        {
            result.ShouldHaveValidationErrorFor(command => command.Provider);
        }
    }

    [Fact]
    public void An_unknown_or_disabled_provider_passes_here_and_gets_the_handlers_404()
    {
        _sut.TestValidate(Proof("nonesuch")).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Command_text_never_shows_a_provider_proof()
    {
        Proof().ToString().ShouldBe(nameof(StepUpCommand));
    }

    private static StepUpCommand Proof(string provider = "google") => new(null, null, null, provider, IdToken, null, Nonce);

    private static ExternalProvider Provider(string name, ExternalProviderKind kind) => new(
        name,
        name,
        kind,
        null,
        [],
        ["client"],
        ExternalEmailRule.EmailVerifiedClaim,
        TrustEmail: false,
        TenantId: null,
        AppId: null,
        AppSecret: null);
}
