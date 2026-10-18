using System.Reflection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Authentication;
using TemplateName.Modules.Auth.Application.Mfa;
using TemplateName.Modules.Auth.Application.Mfa.Verify;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Users.Events;
using TemplateName.SharedKernel;
using TemplateName.UnitTests.Application;

namespace TemplateName.UnitTests.Auth.Mfa;

public sealed class VerifyMfaCommandHandlerTests
{
    private const string ChallengeTokenValue = "challenge-token-value-of-forty-three-chars";
    private const string RefreshTokenValue = "refresh-token-value-of-forty-three-chars-xx";
    private const string AccessTokenValue = "header.payload.signature";
    private const string ProtectedSecret = "protected-secret";
    private const string DeviceName = "Alice's phone";

    private static readonly DateTimeOffset Now = new(2026, 10, 17, 9, 0, 10, 500, TimeSpan.Zero);
    private static readonly DateTimeOffset IssuedAt = Now.AddMinutes(-1);
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    private static readonly byte[] ChallengeHash = [.. Enumerable.Range(1, 32).Select(value => (byte)value)];
    private static readonly byte[] RefreshTokenHash = [.. Enumerable.Range(101, 32).Select(value => (byte)value)];

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IMfaChallengeRepository _challenges = Substitute.For<IMfaChallengeRepository>();
    private readonly IMfaMethodRepository _methods = Substitute.For<IMfaMethodRepository>();
    private readonly IRecoveryCodeRepository _recoveryCodes = Substitute.For<IRecoveryCodeRepository>();
    private readonly IMfaSecretProtector _secretProtector = Substitute.For<IMfaSecretProtector>();
    private readonly ISessionRepository _sessions = Substitute.For<ISessionRepository>();
    private readonly ISecureTokenService _tokenService = Substitute.For<ISecureTokenService>();
    private readonly IAccessTokenIssuer _accessTokenIssuer = Substitute.For<IAccessTokenIssuer>();
    private readonly IClientContext _clientContext = Substitute.For<IClientContext>();
    private readonly IAuthAuditWriter _auditWriter = Substitute.For<IAuthAuditWriter>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IAuthMetrics _metrics = Substitute.For<IAuthMetrics>();
    private readonly List<AuthAuditLog> _auditEntries = [];
    private readonly List<UserSession> _addedSessions = [];
    private readonly List<AccessTokenRequest> _tokenRequests = [];
    private readonly byte[] _secret = Totp.NewSecret();
    private readonly User _user;
    private readonly MfaChallenge _challenge;
    private readonly VerifyMfaCommandHandler _sut;

    public VerifyMfaCommandHandlerTests()
    {
        _user = NewUser();
        _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_user);

        // The database counts atomically; User.RecordFailedSignIn is the specification of that count (as in LoginCommandHandlerTests).
        _users.RecordFailedSignInAsync(_user.Id, Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var now = call.ArgAt<DateTimeOffset>(1);
                if (_user.IsLockedOut(now))
                {
                    return (FailedSignIn?)null;
                }

                _user.RecordFailedSignIn(now, call.ArgAt<int>(2), call.ArgAt<TimeSpan>(3));
                _user.ClearDomainEvents();
                return new FailedSignIn(_user.AccessFailedCount, _user.LockoutEnd);
            });

        _challenge = MfaChallenge.Issue(_user.Id, ChallengeHash, _user.SecurityStamp, DeviceName, TimeSpan.FromMinutes(5), IssuedAt);
        _tokenService.Hash(ChallengeTokenValue).Returns(ChallengeHash);
        _tokenService.Generate().Returns(new GeneratedToken(RefreshTokenValue, RefreshTokenHash));
        _challenges.GetByTokenHashAsync(ChallengeHash, Arg.Any<CancellationToken>()).Returns(_challenge);
        _challenges.TryConsumeAsync(_challenge.Id, Now, 5, Arg.Any<CancellationToken>()).Returns(true);
        _challenges.RecordFailedAttemptAsync(_challenge.Id, Arg.Any<CancellationToken>()).Returns(1);

        _methods.HasConfirmedAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(true);
        var method = UserMfaMethod.StartTotp(_user.Id, "Phone", ProtectedSecret, IssuedAt.AddDays(-1));
        method.Confirm(Totp.TimeStepAt(IssuedAt.AddDays(-1)), IssuedAt.AddDays(-1)).IsSuccess.ShouldBeTrue();
        _methods.GetTotpAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(method);
        _methods.TryClaimTimeStepAsync(method.Id, Arg.Any<long>(), Now, Arg.Any<CancellationToken>()).Returns(true);
        _secretProtector.Unprotect(ProtectedSecret).Returns(_ => _secret.ToArray());

        _accessTokenIssuer.Issue(Arg.Do<AccessTokenRequest>(_tokenRequests.Add)).Returns(new AccessToken(AccessTokenValue, Now.AddMinutes(10)));
        _clientContext.IpAddress.Returns("203.0.113.7");
        _clientContext.UserAgent.Returns("UnitTests/1.0");
        _auditWriter.Record(Arg.Do<AuthAuditLog>(_auditEntries.Add));
        _sessions.Add(Arg.Do<UserSession>(_addedSessions.Add));
        _unitOfWork.TrySaveChangesAsync(Arg.Any<CancellationToken>()).Returns(true);

        var verifier = new MfaFactorVerifier(_methods, _recoveryCodes, _secretProtector, new RecordingLogger<MfaFactorVerifier>());
        var sessionStarter = new SessionStarter(_sessions, _tokenService, _accessTokenIssuer, _clientContext, Options.Create(new RefreshTokenOptions()));
        _sut = new VerifyMfaCommandHandler(
            _users,
            _challenges,
            _methods,
            verifier,
            sessionStarter,
            _tokenService,
            _auditWriter,
            _unitOfWork,
            _metrics,
            Options.Create(new LockoutOptions()),
            Options.Create(new MfaOptions()),
            new FakeTimeProvider(Now));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string CurrentCode => Totp.ComputeCode(_secret, Totp.TimeStepAt(Now));

    [Fact]
    public async Task Totp_signs_in_with_pwd_otp_mfa()
    {
        FailBeforehand(times: 3);

        var result = await _sut.HandleAsync(Command(code: CurrentCode), Ct);

        var session = _addedSessions.ShouldHaveSingleItem();
        result.Value.ShouldBe(new MfaVerifyResponse(AccessTokenValue, Now.AddMinutes(10), RefreshTokenValue, Now.AddDays(14), session.Id, null, null));
        session.UserId.ShouldBe(_user.Id);
        session.AuthMethods.ShouldBe("pwd otp mfa");
        session.AuthenticatedAt.ShouldBe(Now);
        session.DeviceName.ShouldBe(DeviceName);
        session.UserAgent.ShouldBe("UnitTests/1.0");
        session.IpAddress.ShouldBe("203.0.113.7");
        session.SecurityStamp.ShouldBe(_user.SecurityStamp);
        session.RefreshTokens.ShouldHaveSingleItem().TokenHash.ShouldBe(RefreshTokenHash);
        _tokenRequests.ShouldHaveSingleItem().ShouldBe(new AccessTokenRequest(_user.Id, session.Id, _user.SecurityStamp, "pwd otp mfa", Now, "ms"));

        // The second factor is the successful sign-in: it resets the failures the password step left alone (D7).
        _user.AccessFailedCount.ShouldBe(0);
        _user.LastLoginAt.ShouldBe(Now);
        _user.DomainEvents.ShouldBeEmpty();
        await _challenges.Received(1).TryConsumeAsync(_challenge.Id, Now, 5, CancellationToken.None);
        await _challenges.DidNotReceiveWithAnyArgs().RecordFailedAttemptAsync(default, Ct);
        await _users.DidNotReceiveWithAnyArgs().RecordFailedSignInAsync(default, default, default, default, Ct);
        _auditEntries.Select(entry => entry.EventType).ShouldBe([AuthAuditEvents.MfaVerified, AuthAuditEvents.LoginSucceeded]);
        _auditEntries.ShouldAllBe(entry => entry.Succeeded && entry.UserId == _user.Id && entry.SessionId == session.Id);
        _auditEntries[0].Details.ShouldBe("""{"factor":"totp"}""");
        await _unitOfWork.Received(1).TrySaveChangesAsync(CancellationToken.None);
        _metrics.Received(1).RecordMfaVerification(MfaVerificationOutcome.Succeeded, MfaFactor.Totp);
    }

    [Fact]
    public async Task Recovery_code_signs_in_with_pwd_rcvr_mfa_and_notes_the_use()
    {
        var code = RecoveryCode.Create(_user.Id, RecoveryCodeFormat.Hash("ABCDEFGHJKMNPQRS"), IssuedAt);
        _recoveryCodes.FindUnusedAsync(_user.Id, Arg.Is<byte[]>(hash => hash.SequenceEqual(code.CodeHash)), Arg.Any<CancellationToken>()).Returns(code);
        _recoveryCodes.TryUseAsync(code.Id, Now, CancellationToken.None).Returns(true);

        var result = await _sut.HandleAsync(Command(recoveryCode: "abcd efgh jkmn pqrs"), Ct);

        result.IsSuccess.ShouldBeTrue();
        var session = _addedSessions.ShouldHaveSingleItem();
        session.AuthMethods.ShouldBe("pwd rcvr mfa");
        _tokenRequests.ShouldHaveSingleItem().AuthMethods.ShouldBe("pwd rcvr mfa");
        _user.DomainEvents.ShouldHaveSingleItem().ShouldBe(new MfaChangedDomainEvent(_user.Id, MfaChange.RecoveryCodeUsed));
        _auditEntries[0].EventType.ShouldBe(AuthAuditEvents.MfaVerified);
        _auditEntries[0].Details.ShouldBe("""{"factor":"recovery_code"}""");
        await _methods.DidNotReceiveWithAnyArgs().GetTotpAsync(default, Ct);
        _metrics.Received(1).RecordMfaVerification(MfaVerificationOutcome.Succeeded, MfaFactor.RecoveryCode);
    }

    [Fact]
    public async Task Wrong_code_counts_on_the_challenge_and_the_user()
    {
        var wrongCode = Totp.ComputeCode(_secret, Totp.TimeStepAt(Now) + 2);

        var result = await _sut.HandleAsync(Command(code: wrongCode), Ct);

        result.Error.ShouldBe(MfaErrors.InvalidCode);
        await _challenges.Received(1).RecordFailedAttemptAsync(_challenge.Id, CancellationToken.None);
        await _users.Received(1).RecordFailedSignInAsync(_user.Id, Now, 5, LockoutDuration, CancellationToken.None);
        _user.AccessFailedCount.ShouldBe(1);
        _user.DomainEvents.ShouldBeEmpty();
        await _methods.DidNotReceiveWithAnyArgs().TryClaimTimeStepAsync(default, default, default, Ct);
        await _challenges.DidNotReceiveWithAnyArgs().TryConsumeAsync(default, default, default, Ct);
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.MfaFailed);
        audit.Succeeded.ShouldBeFalse();
        audit.UserId.ShouldBe(_user.Id);
        audit.FailureReason.ShouldBe("auth.invalid_mfa_code");
        audit.Details.ShouldBe("""{"factor":"totp"}""");
        await _unitOfWork.Received(1).SaveChangesAsync(CancellationToken.None);
        await _unitOfWork.DidNotReceiveWithAnyArgs().TrySaveChangesAsync(Ct);
        _addedSessions.ShouldBeEmpty();
        _metrics.Received(1).RecordMfaVerification(MfaVerificationOutcome.InvalidCode, MfaFactor.Totp);
    }

    [Fact]
    public async Task Fifth_wrong_code_locks_and_raises_the_lockout_event()
    {
        FailBeforehand(times: 4);

        var result = await _sut.HandleAsync(Command(recoveryCode: "ABCD-EFGH-JKMN-PQRS"), Ct);

        result.Error.ShouldBe(MfaErrors.InvalidCode);
        _user.AccessFailedCount.ShouldBe(5);
        _user.LockoutEnd.ShouldBe(Now + LockoutDuration);
        _user.DomainEvents.ShouldHaveSingleItem().ShouldBe(new UserLockedOutDomainEvent(_user.Id, Now + LockoutDuration));
        _auditEntries.Select(entry => entry.EventType).ShouldBe([AuthAuditEvents.MfaFailed, AuthAuditEvents.LockedOut]);
        var lockedOut = _auditEntries[1];
        lockedOut.Succeeded.ShouldBeFalse();
        lockedOut.UserId.ShouldBe(_user.Id);
        lockedOut.Details.ShouldNotBeNull().ShouldContain("2026-10-17T09:15:10.5");
        await _unitOfWork.Received(1).SaveChangesAsync(CancellationToken.None);
        _metrics.Received(1).RecordMfaVerification(MfaVerificationOutcome.InvalidCode, MfaFactor.RecoveryCode);
    }

    [Fact]
    public async Task Challenge_of_a_changed_stamp_is_invalid()
    {
        _user.ChangePassword("changed-hash", historyCount: 5, IssuedAt.AddSeconds(30));
        _user.ClearDomainEvents();

        var result = await _sut.HandleAsync(Command(code: CurrentCode), Ct);

        await AssertInvalidChallengeAndNothingCountedAsync(result.Error, expectedUserId: _user.Id);
    }

    [Theory]
    [InlineData("unknown token")]
    [InlineData("expired")]
    [InlineData("consumed")]
    [InlineData("exhausted")]
    [InlineData("user gone")]
    [InlineData("suspended")]
    [InlineData("locked")]
    [InlineData("no confirmed method")]
    public async Task Unusable_challenge_is_invalid_and_nothing_is_counted(string state)
    {
        Guid? expectedUserId = _user.Id;
        var command = Command(code: CurrentCode);
        switch (state)
        {
            case "unknown token":
                command = command with { ChallengeToken = "another-token" };
                _tokenService.Hash("another-token").Returns([.. Enumerable.Repeat((byte)7, 32)]);
                expectedUserId = null;
                break;
            case "expired":
                Set(_challenge, nameof(MfaChallenge.ExpiresAt), Now);
                break;
            case "consumed":
                Set(_challenge, nameof(MfaChallenge.ConsumedAt), (DateTimeOffset?)Now.AddSeconds(-1));
                break;
            case "exhausted":
                Set(_challenge, nameof(MfaChallenge.FailedAttempts), 5);
                break;
            case "user gone":
                _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns((User?)null);
                break;
            case "suspended":
                _user.Suspend(IssuedAt);
                break;
            case "locked":
                FailBeforehand(times: 5);
                break;
            default:
                _methods.HasConfirmedAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(false);
                break;
        }

        var result = await _sut.HandleAsync(command, Ct);

        await AssertInvalidChallengeAndNothingCountedAsync(result.Error, expectedUserId);
    }

    [Fact]
    public async Task Lost_consume_is_invalid_challenge()
    {
        _challenges.TryConsumeAsync(_challenge.Id, Now, 5, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.HandleAsync(Command(code: CurrentCode), Ct);

        // The code is spent (its step is claimed) but the challenge was consumed, exhausted or expired meanwhile: the user signs in again.
        result.Error.ShouldBe(MfaErrors.InvalidChallenge);
        await _methods.Received(1).TryClaimTimeStepAsync(Arg.Any<Guid>(), Totp.TimeStepAt(Now), Now, CancellationToken.None);
        await _challenges.Received(1).TryConsumeAsync(_challenge.Id, Now, 5, CancellationToken.None);
        await _users.DidNotReceiveWithAnyArgs().RecordFailedSignInAsync(default, default, default, default, Ct);
        _addedSessions.ShouldBeEmpty();
        _user.LastLoginAt.ShouldBeNull();
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.MfaFailed);
        audit.FailureReason.ShouldBe("auth.invalid_mfa_challenge");
        await _unitOfWork.Received(1).SaveChangesAsync(CancellationToken.None);
        _metrics.Received(1).RecordMfaVerification(MfaVerificationOutcome.InvalidChallenge, null);
    }

    [Fact]
    public async Task Save_conflict_is_retried_once_with_the_user_read_again()
    {
        // A simultaneous request with the same code lost its claim and counted a failure on the user row between our read and our save.
        var reread = NewUser(id: _user.Id, stamp: _user.SecurityStamp);
        _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_user, reread);
        _unitOfWork.TrySaveChangesAsync(Arg.Any<CancellationToken>()).Returns(false, true);

        var result = await _sut.HandleAsync(Command(code: CurrentCode), Ct);

        // The code and the challenge are already ours, so the sign-in completes on the fresh row; the first attempt's changes were discarded.
        result.IsSuccess.ShouldBeTrue();
        _addedSessions.Count.ShouldBe(2);
        result.Value.SessionId.ShouldBe(_addedSessions[1].Id);
        _addedSessions[1].UserId.ShouldBe(_user.Id);
        reread.LastLoginAt.ShouldBe(Now);
        await _methods.Received(1).TryClaimTimeStepAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await _challenges.Received(1).TryConsumeAsync(_challenge.Id, Now, 5, CancellationToken.None);
        await _unitOfWork.Received(2).TrySaveChangesAsync(CancellationToken.None);
        _auditEntries.Select(entry => entry.EventType)
            .ShouldBe([AuthAuditEvents.MfaVerified, AuthAuditEvents.LoginSucceeded, AuthAuditEvents.MfaVerified, AuthAuditEvents.LoginSucceeded]);
        _metrics.Received(1).RecordMfaVerification(MfaVerificationOutcome.Succeeded, MfaFactor.Totp);
    }

    [Theory]
    [InlineData("conflict again")]
    [InlineData("stamp changed")]
    [InlineData("locked")]
    [InlineData("user gone")]
    public async Task Save_conflict_that_cannot_be_retried_is_invalid_challenge(string state)
    {
        var reread = NewUser(id: _user.Id, stamp: state == "stamp changed" ? "another-stamp" : _user.SecurityStamp);
        if (state == "locked")
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                reread.RecordFailedSignIn(Now.AddSeconds(-1), maxFailedAttempts: 5, LockoutDuration);
            }
        }

        _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_user, state == "user gone" ? null : reread);
        _unitOfWork.TrySaveChangesAsync(Arg.Any<CancellationToken>()).Returns(false, state == "conflict again" ? false : true);

        var result = await _sut.HandleAsync(Command(code: CurrentCode), Ct);

        // The challenge is spent: the user signs in again (and never sees a 409 or a 500).
        result.Error.ShouldBe(MfaErrors.InvalidChallenge);
        await _unitOfWork.Received(state == "conflict again" ? 2 : 1).TrySaveChangesAsync(CancellationToken.None);
        await _users.DidNotReceiveWithAnyArgs().RecordFailedSignInAsync(default, default, default, default, Ct);
        _metrics.Received(1).RecordMfaVerification(MfaVerificationOutcome.InvalidChallenge, null);
    }

    [Fact]
    public void Command_and_response_text_never_show_a_secret()
    {
        Command(code: "123456").ToString().ShouldBe(nameof(VerifyMfaCommand));
        new MfaVerifyResponse(AccessTokenValue, Now, RefreshTokenValue, Now, Guid.Empty, "trusted-device-token", Now).ToString()
            .ShouldBe(nameof(MfaVerifyResponse));
    }

    private static VerifyMfaCommand Command(string? code = null, string? recoveryCode = null) =>
        new(ChallengeTokenValue, code, recoveryCode, RememberDevice: false);

    private static User NewUser(Guid? id = null, string? stamp = null)
    {
        var user = User.Register("alice@example.com", "Alice", "ms", "stored-hash", IssuedAt.AddDays(-1)).Value;
        user.ConfirmEmail(IssuedAt.AddDays(-1));
        user.ClearDomainEvents();
        if (id is { } userId)
        {
            Set(user, nameof(User.Id), userId);
        }

        if (stamp is not null)
        {
            Set(user, nameof(User.SecurityStamp), stamp);
        }

        return user;
    }

    // Sets a state the domain only reaches through the database (a consumed or exhausted challenge) or another aggregate's id; the
    // property is looked up on the type that declares it, so a private setter of a base class is found too.
    private static void Set<T>(object target, string propertyName, T value)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var declaringType = target.GetType().GetProperty(propertyName, Flags)!.DeclaringType!;
        declaringType.GetProperty(propertyName, Flags)!.SetValue(target, value);
    }

    // Failures counted ten seconds ago, so a lockout they cause is still running at Now.
    private void FailBeforehand(int times)
    {
        for (var attempt = 0; attempt < times; attempt++)
        {
            _user.RecordFailedSignIn(Now.AddSeconds(-10), maxFailedAttempts: 5, LockoutDuration);
        }

        _user.ClearDomainEvents();
    }

    private async Task AssertInvalidChallengeAndNothingCountedAsync(Error error, Guid? expectedUserId)
    {
        error.ShouldBe(MfaErrors.InvalidChallenge);

        // Nothing is verified (no code is spent) and nothing is counted, on the challenge or the user.
        await _methods.DidNotReceiveWithAnyArgs().GetTotpAsync(default, Ct);
        await _methods.DidNotReceiveWithAnyArgs().TryClaimTimeStepAsync(default, default, default, Ct);
        await _recoveryCodes.DidNotReceiveWithAnyArgs().FindUnusedAsync(default, default!, Ct);
        await _challenges.DidNotReceiveWithAnyArgs().RecordFailedAttemptAsync(default, Ct);
        await _challenges.DidNotReceiveWithAnyArgs().TryConsumeAsync(default, default, default, Ct);
        await _users.DidNotReceiveWithAnyArgs().RecordFailedSignInAsync(default, default, default, default, Ct);
        await _unitOfWork.Received(1).SaveChangesAsync(Ct);
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.MfaFailed);
        audit.Succeeded.ShouldBeFalse();
        audit.UserId.ShouldBe(expectedUserId);
        audit.FailureReason.ShouldBe("auth.invalid_mfa_challenge");
        audit.Details.ShouldBe("""{"factor":"totp"}""");
        _addedSessions.ShouldBeEmpty();
        _accessTokenIssuer.DidNotReceiveWithAnyArgs().Issue(default!);
        _metrics.Received(1).RecordMfaVerification(MfaVerificationOutcome.InvalidChallenge, null);
    }
}
