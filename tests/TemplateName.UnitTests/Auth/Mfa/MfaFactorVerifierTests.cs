using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Mfa;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.UnitTests.Application;

namespace TemplateName.UnitTests.Auth.Mfa;

public sealed class MfaFactorVerifierTests
{
    private const string ProtectedSecret = "protected-secret-value";
    private const string DisplayedRecoveryCode = "ABCD-EFGH-JKMN-PQRS";

    private static readonly DateTimeOffset Now = new(2026, 10, 17, 9, 0, 10, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly IMfaMethodRepository _methods = Substitute.For<IMfaMethodRepository>();
    private readonly IRecoveryCodeRepository _recoveryCodes = Substitute.For<IRecoveryCodeRepository>();
    private readonly IMfaSecretProtector _secretProtector = Substitute.For<IMfaSecretProtector>();
    private readonly RecordingLogger<MfaFactorVerifier> _logger = new();
    private readonly byte[] _secret = Totp.NewSecret();
    private readonly UserMfaMethod _method;
    private readonly RecoveryCode _code;
    private readonly MfaFactorVerifier _sut;

    public MfaFactorVerifierTests()
    {
        // Confirmed a day ago, so today's steps are all greater than its last used step.
        _method = UserMfaMethod.StartTotp(UserId, "Phone", ProtectedSecret, Now.AddDays(-1));
        _method.Confirm(Totp.TimeStepAt(Now.AddDays(-1)), Now.AddDays(-1)).IsSuccess.ShouldBeTrue();
        _methods.GetTotpAsync(UserId, Arg.Any<CancellationToken>()).Returns(_method);

        // A fresh copy per call: the verifier zeroes the bytes it was given.
        _secretProtector.Unprotect(ProtectedSecret).Returns(_ => _secret.ToArray());

        _code = RecoveryCode.Create(UserId, RecoveryCodeFormat.Hash("ABCDEFGHJKMNPQRS"), Now.AddDays(-1));
        _recoveryCodes
            .FindUnusedAsync(UserId, Arg.Is<byte[]>(hash => hash.SequenceEqual(_code.CodeHash)), Arg.Any<CancellationToken>())
            .Returns(_code);

        _sut = new MfaFactorVerifier(_methods, _recoveryCodes, _secretProtector, _logger);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string CurrentCode => Totp.ComputeCode(_secret, Totp.TimeStepAt(Now));

    [Fact]
    public async Task Totp_code_succeeds_only_when_the_claim_wins()
    {
        _methods.TryClaimTimeStepAsync(_method.Id, Arg.Any<long>(), Now, Arg.Any<CancellationToken>()).Returns(true);

        var factor = await _sut.VerifyAsync(UserId, new MfaProof(CurrentCode, null), Now, Ct);

        factor.ShouldBe(MfaFactor.Totp);

        // The claim is the step that matched, and it is not cancellable: once it may have committed, the outcome must be stored.
        await _methods.Received(1).TryClaimTimeStepAsync(_method.Id, Totp.TimeStepAt(Now), Now, CancellationToken.None);
        await _recoveryCodes.DidNotReceiveWithAnyArgs().FindUnusedAsync(default, default!, Ct);
    }

    [Fact]
    public async Task Lost_claim_fails()
    {
        _methods.TryClaimTimeStepAsync(_method.Id, Arg.Any<long>(), Now, Arg.Any<CancellationToken>()).Returns(false);

        var factor = await _sut.VerifyAsync(UserId, new MfaProof(CurrentCode, null), Now, Ct);

        factor.ShouldBeNull();
        await _methods.Received(1).TryClaimTimeStepAsync(_method.Id, Totp.TimeStepAt(Now), Now, CancellationToken.None);
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    public async Task Wrong_or_malformed_code_fails_without_a_claim(string kind)
    {
        // A code two steps away never matches; the other inputs are not 6 digits.
        var code = kind == "wrong" ? Totp.ComputeCode(_secret, Totp.TimeStepAt(Now) + 2) : kind;

        var factor = await _sut.VerifyAsync(UserId, new MfaProof(code, null), Now, Ct);

        factor.ShouldBeNull();
        await _methods.DidNotReceiveWithAnyArgs().TryClaimTimeStepAsync(default, default, default, Ct);
    }

    [Fact]
    public async Task Code_of_a_step_already_used_fails_without_a_claim()
    {
        var used = UserMfaMethod.StartTotp(UserId, "Phone", ProtectedSecret, Now.AddMinutes(-1));
        used.Confirm(Totp.TimeStepAt(Now), Now).IsSuccess.ShouldBeTrue();
        _methods.GetTotpAsync(UserId, Arg.Any<CancellationToken>()).Returns(used);

        var factor = await _sut.VerifyAsync(UserId, new MfaProof(CurrentCode, null), Now, Ct);

        factor.ShouldBeNull();
        await _methods.DidNotReceiveWithAnyArgs().TryClaimTimeStepAsync(default, default, default, Ct);
    }

    [Fact]
    public async Task Missing_or_pending_method_fails()
    {
        _methods.GetTotpAsync(UserId, Arg.Any<CancellationToken>()).Returns((UserMfaMethod?)null);
        (await _sut.VerifyAsync(UserId, new MfaProof(CurrentCode, null), Now, Ct)).ShouldBeNull();

        _methods.GetTotpAsync(UserId, Arg.Any<CancellationToken>()).Returns(UserMfaMethod.StartTotp(UserId, "Phone", ProtectedSecret, Now));
        (await _sut.VerifyAsync(UserId, new MfaProof(CurrentCode, null), Now, Ct)).ShouldBeNull();

        _secretProtector.DidNotReceiveWithAnyArgs().Unprotect(default!);
        await _methods.DidNotReceiveWithAnyArgs().TryClaimTimeStepAsync(default, default, default, Ct);
    }

    [Fact]
    public async Task Unreadable_secret_fails_and_logs_the_method_id_only()
    {
        _secretProtector.Unprotect(ProtectedSecret).Throws(new CryptographicException("The key was not found in the key ring."));

        var factor = await _sut.VerifyAsync(UserId, new MfaProof(CurrentCode, null), Now, Ct);

        factor.ShouldBeNull();
        await _methods.DidNotReceiveWithAnyArgs().TryClaimTimeStepAsync(default, default, default, Ct);
        var (level, message, properties) = _logger.Entries.ShouldHaveSingleItem();
        level.ShouldBe(LogLevel.Error);
        message.ShouldBe($"Could not read the TOTP secret of MFA method {_method.Id}");
        properties.Keys.Where(key => key != "{OriginalFormat}").ShouldBe(["MethodId"]);
        properties["MethodId"].ShouldBe(_method.Id);
    }

    [Theory]
    [InlineData(DisplayedRecoveryCode)]
    [InlineData("abcd-efgh-jkmn-pqrs")]
    [InlineData("abcdefghjkmnpqrs")]
    [InlineData(" ABCD EFGH JKMN PQRS\n")]
    public async Task Recovery_code_succeeds_once_in_any_spelling(string spelling)
    {
        _recoveryCodes.TryUseAsync(_code.Id, Now, Arg.Any<CancellationToken>()).Returns(true, false);

        var first = await _sut.VerifyAsync(UserId, new MfaProof(null, spelling), Now, Ct);
        var second = await _sut.VerifyAsync(UserId, new MfaProof(null, DisplayedRecoveryCode), Now, Ct);

        first.ShouldBe(MfaFactor.RecoveryCode);
        second.ShouldBeNull();
        await _recoveryCodes.Received(2).TryUseAsync(_code.Id, Now, CancellationToken.None);
        await _methods.DidNotReceiveWithAnyArgs().GetTotpAsync(default, Ct);
    }

    [Theory]
    [InlineData("ABCD-EFGH-JKMN-PQR")]
    [InlineData("ABCD-EFGH-JKMN-PQRSO")]
    [InlineData("ABCD-EFGH-JKMN-PQR0")]
    [InlineData("")]
    public async Task Garbled_recovery_code_fails_without_a_lookup(string garbled)
    {
        var factor = await _sut.VerifyAsync(UserId, new MfaProof(null, garbled), Now, Ct);

        factor.ShouldBeNull();
        await _recoveryCodes.DidNotReceiveWithAnyArgs().FindUnusedAsync(default, default!, Ct);
        await _recoveryCodes.DidNotReceiveWithAnyArgs().TryUseAsync(default, default, Ct);
    }

    [Fact]
    public async Task Unknown_or_used_recovery_code_fails_without_a_claim()
    {
        var factor = await _sut.VerifyAsync(UserId, new MfaProof(null, "ZZZZ-ZZZZ-ZZZZ-ZZZZ"), Now, Ct);

        factor.ShouldBeNull();
        await _recoveryCodes.DidNotReceiveWithAnyArgs().TryUseAsync(default, default, Ct);
    }

    [Fact]
    public async Task Proof_with_both_or_neither_value_fails()
    {
        (await _sut.VerifyAsync(UserId, new MfaProof(CurrentCode, DisplayedRecoveryCode), Now, Ct)).ShouldBeNull();
        (await _sut.VerifyAsync(UserId, new MfaProof(null, null), Now, Ct)).ShouldBeNull();

        await _methods.DidNotReceiveWithAnyArgs().GetTotpAsync(default, Ct);
        await _recoveryCodes.DidNotReceiveWithAnyArgs().FindUnusedAsync(default, default!, Ct);
    }

    [Fact]
    public async Task Nothing_logged_contains_the_code_or_the_secret()
    {
        // Every path into the one recording logger (it records every level): a won and a lost claim, a wrong code, an unreadable
        // secret, a used recovery code and an unknown one.
        _methods.TryClaimTimeStepAsync(_method.Id, Arg.Any<long>(), Now, Arg.Any<CancellationToken>()).Returns(true, false);
        _recoveryCodes.TryUseAsync(_code.Id, Now, Arg.Any<CancellationToken>()).Returns(true, false);
        var wrongCode = Totp.ComputeCode(_secret, Totp.TimeStepAt(Now) - 2);

        await _sut.VerifyAsync(UserId, new MfaProof(CurrentCode, null), Now, Ct);
        await _sut.VerifyAsync(UserId, new MfaProof(CurrentCode, null), Now, Ct);
        await _sut.VerifyAsync(UserId, new MfaProof(wrongCode, null), Now, Ct);
        await _sut.VerifyAsync(UserId, new MfaProof(null, DisplayedRecoveryCode), Now, Ct);
        await _sut.VerifyAsync(UserId, new MfaProof(null, DisplayedRecoveryCode), Now, Ct);
        await _sut.VerifyAsync(UserId, new MfaProof(null, "ZZZZ-ZZZZ-ZZZZ-ZZZZ"), Now, Ct);
        _secretProtector.Unprotect(ProtectedSecret).Throws(new CryptographicException("Unreadable."));
        await _sut.VerifyAsync(UserId, new MfaProof(CurrentCode, null), Now, Ct);

        _logger.Entries.ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Error);
        string[] secrets =
        [
            CurrentCode,
            wrongCode,
            DisplayedRecoveryCode,
            "ABCDEFGHJKMNPQRS",
            ProtectedSecret,
            Base32.Encode(_secret),
            Convert.ToHexString(_secret),
            Convert.ToBase64String(_secret),
        ];
        foreach (var (_, message, properties) in _logger.Entries)
        {
            var text = message + " " + string.Join(' ', properties.Values);
            foreach (var secret in secrets)
            {
                text.ShouldNotContain(secret, Case.Insensitive);
            }
        }
    }

    [Fact]
    public void Proof_text_never_shows_a_code()
    {
        var proof = new MfaProof("123456", DisplayedRecoveryCode);

        proof.ToString().ShouldBe(nameof(MfaProof));
    }
}
