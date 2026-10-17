using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Authentication;
using TemplateName.Modules.Auth.Application.Mfa;
using TemplateName.Modules.Auth.Application.Passwords;
using TemplateName.Modules.Auth.Application.StepUp;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Users.Events;
using TemplateName.SharedKernel;
using TemplateName.UnitTests.Application;

namespace TemplateName.UnitTests.Auth.Mfa;

public sealed class StepUpCommandHandlerTests
{
    private const string Password = "correct horse battery";
    private const string StoredHash = "stored-hash";
    private const string ProtectedSecret = "protected-secret";
    private const string AccessTokenValue = "header.payload.signature";
    private const string RecoveryCodeText = "ABCD-EFGH-JKMN-PQRS";

    // Not on a whole second, so the test sees that the step-up expiry counts from the token's whole-second auth_time.
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 9, 0, 10, 500, TimeSpan.Zero);
    private static readonly DateTimeOffset SignedInAt = Now.AddHours(-2);
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly ISessionRepository _sessions = Substitute.For<ISessionRepository>();
    private readonly IMfaMethodRepository _methods = Substitute.For<IMfaMethodRepository>();
    private readonly IRecoveryCodeRepository _recoveryCodes = Substitute.For<IRecoveryCodeRepository>();
    private readonly IMfaSecretProtector _secretProtector = Substitute.For<IMfaSecretProtector>();
    private readonly IPasswordHasher _passwordHasher = Substitute.For<IPasswordHasher>();
    private readonly IAccessTokenIssuer _accessTokenIssuer = Substitute.For<IAccessTokenIssuer>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IAuthAuditWriter _auditWriter = Substitute.For<IAuthAuditWriter>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IAuthMetrics _metrics = Substitute.For<IAuthMetrics>();
    private readonly List<AuthAuditLog> _auditEntries = [];
    private readonly List<AccessTokenRequest> _tokenRequests = [];
    private readonly byte[] _secret = Totp.NewSecret();
    private readonly User _user;
    private readonly UserSession _session;
    private readonly StepUpCommandHandler _sut;

    public StepUpCommandHandlerTests()
    {
        _user = User.Register("alice@example.com", "Alice", "ms", StoredHash, SignedInAt.AddDays(-1)).Value;
        _user.ConfirmEmail(SignedInAt.AddDays(-1));
        _user.ClearDomainEvents();
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

        (_session, _) = UserSession.Start(
            _user.Id,
            AuthenticationMethods.Password,
            "Phone",
            userAgent: null,
            ipAddress: null,
            _user.SecurityStamp,
            [.. Enumerable.Range(1, 32).Select(value => (byte)value)],
            TimeSpan.FromDays(14),
            TimeSpan.FromDays(90),
            SignedInAt);
        _sessions.GetByIdAsync(_session.Id, Arg.Any<CancellationToken>()).Returns(_session);

        _currentUser.UserId.Returns(_user.Id);
        _currentUser.SessionId.Returns(_session.Id);
        _accessTokenIssuer.Issue(Arg.Do<AccessTokenRequest>(_tokenRequests.Add)).Returns(new AccessToken(AccessTokenValue, Now.AddMinutes(10)));
        _auditWriter.Record(Arg.Do<AuthAuditLog>(_auditEntries.Add));
        _passwordHasher.Verify(StoredHash, Password).Returns(PasswordVerification.Success);

        var verifier = new MfaFactorVerifier(_methods, _recoveryCodes, _secretProtector, new RecordingLogger<MfaFactorVerifier>());
        _sut = new StepUpCommandHandler(
            _users,
            _sessions,
            _methods,
            verifier,
            _passwordHasher,
            _accessTokenIssuer,
            _currentUser,
            _auditWriter,
            _unitOfWork,
            _metrics,
            Options.Create(new LockoutOptions()),
            Options.Create(new StepUpOptions()),
            new FakeTimeProvider(Now));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string CurrentCode => Totp.ComputeCode(_secret, Totp.TimeStepAt(Now));

    [Fact]
    public async Task Password_step_up_replaces_amr_and_auth_time()
    {
        var result = await _sut.HandleAsync(new StepUpCommand(Password, null, null), Ct);

        // The token's auth_time is Now's whole second, so the step-up ends MaxAge after that second.
        var whole = DateTimeOffset.FromUnixTimeSeconds(Now.ToUnixTimeSeconds());
        result.Value.ShouldBe(new StepUpResponse(AccessTokenValue, Now.AddMinutes(10), whole.AddMinutes(10)));
        _session.AuthMethods.ShouldBe("pwd");
        _session.AuthenticatedAt.ShouldBe(Now);
        _session.CreatedAt.ShouldBe(SignedInAt);
        _tokenRequests.ShouldHaveSingleItem().ShouldBe(new AccessTokenRequest(_user.Id, _session.Id, _session.SecurityStamp, "pwd", Now, "ms"));
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.StepUpSucceeded);
        audit.Succeeded.ShouldBeTrue();
        audit.UserId.ShouldBe(_user.Id);
        audit.SessionId.ShouldBe(_session.Id);
        audit.Details.ShouldBe("""{"factor":"password"}""");
        await _unitOfWork.Received(1).SaveChangesAsync(CancellationToken.None);
        await _users.DidNotReceiveWithAnyArgs().RecordFailedSignInAsync(default, default, default, default, Ct);
        _metrics.Received(1).RecordStepUp(true);
    }

    [Fact]
    public async Task Totp_step_up_records_otp_and_claims_the_step()
    {
        GivenMfa();
        _methods.TryClaimTimeStepAsync(Arg.Any<Guid>(), Totp.TimeStepAt(Now), Now, CancellationToken.None).Returns(true);

        var result = await _sut.HandleAsync(new StepUpCommand(null, CurrentCode, null), Ct);

        result.IsSuccess.ShouldBeTrue();
        _session.AuthMethods.ShouldBe("otp");
        _session.AuthenticatedAt.ShouldBe(Now);
        _tokenRequests.ShouldHaveSingleItem().AuthMethods.ShouldBe("otp");
        _auditEntries.ShouldHaveSingleItem().Details.ShouldBe("""{"factor":"totp"}""");
        _user.DomainEvents.ShouldBeEmpty();
        _passwordHasher.DidNotReceiveWithAnyArgs().Verify(default!, default!);
    }

    [Fact]
    public async Task Recovery_code_step_up_notes_the_use()
    {
        GivenMfa();
        var code = RecoveryCode.Create(_user.Id, RecoveryCodeFormat.Hash("ABCDEFGHJKMNPQRS"), SignedInAt);
        _recoveryCodes.FindUnusedAsync(_user.Id, Arg.Is<byte[]>(hash => hash.SequenceEqual(code.CodeHash)), Arg.Any<CancellationToken>()).Returns(code);
        _recoveryCodes.TryUseAsync(code.Id, Now, CancellationToken.None).Returns(true);

        var result = await _sut.HandleAsync(new StepUpCommand(null, null, "abcd efgh jkmn pqrs"), Ct);

        result.IsSuccess.ShouldBeTrue();
        _session.AuthMethods.ShouldBe("rcvr");
        _tokenRequests.ShouldHaveSingleItem().AuthMethods.ShouldBe("rcvr");
        _user.DomainEvents.ShouldHaveSingleItem().ShouldBe(new MfaChangedDomainEvent(_user.Id, MfaChange.RecoveryCodeUsed));
        _auditEntries.ShouldHaveSingleItem().Details.ShouldBe("""{"factor":"recovery_code"}""");
        await _unitOfWork.Received(1).SaveChangesAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Mfa_account_with_a_password_is_method_not_allowed_and_not_counted()
    {
        GivenMfa();

        var result = await _sut.HandleAsync(new StepUpCommand(Password, null, null), Ct);

        result.Error.ShouldBe(MfaErrors.StepUpMethodNotAllowed);
        _passwordHasher.DidNotReceiveWithAnyArgs().Verify(default!, default!);
        await _users.DidNotReceiveWithAnyArgs().RecordFailedSignInAsync(default, default, default, default, Ct);
        _user.AccessFailedCount.ShouldBe(0);
        _session.AuthenticatedAt.ShouldBe(SignedInAt);
        AssertFailureAudit("auth.step_up_method_not_allowed");
        _accessTokenIssuer.DidNotReceiveWithAnyArgs().Issue(default!);
        _metrics.Received(1).RecordStepUp(false);
    }

    [Theory]
    [InlineData("code")]
    [InlineData("recovery")]
    public async Task Password_only_account_with_a_code_is_method_not_allowed_and_not_counted(string proof)
    {
        var command = proof == "code" ? new StepUpCommand(null, "123456", null) : new StepUpCommand(null, null, RecoveryCodeText);

        var result = await _sut.HandleAsync(command, Ct);

        result.Error.ShouldBe(MfaErrors.StepUpMethodNotAllowed);
        await _methods.DidNotReceiveWithAnyArgs().GetTotpAsync(default, Ct);
        await _recoveryCodes.DidNotReceiveWithAnyArgs().FindUnusedAsync(default, default!, Ct);
        await _users.DidNotReceiveWithAnyArgs().RecordFailedSignInAsync(default, default, default, default, Ct);
        AssertFailureAudit("auth.step_up_method_not_allowed");
    }

    [Fact]
    public async Task Wrong_password_counts_a_failed_sign_in_and_audits()
    {
        _passwordHasher.Verify(StoredHash, "wrong password").Returns(PasswordVerification.Failed);

        var result = await _sut.HandleAsync(new StepUpCommand("wrong password", null, null), Ct);

        result.Error.ShouldBe(UserErrors.CurrentPasswordIncorrect);
        _user.AccessFailedCount.ShouldBe(1);
        await _users.Received(1).RecordFailedSignInAsync(_user.Id, Now, 5, LockoutDuration, CancellationToken.None);
        AssertFailureAudit("auth.current_password_incorrect");
        _session.AuthMethods.ShouldBe("pwd");
        _session.AuthenticatedAt.ShouldBe(SignedInAt);
        _accessTokenIssuer.DidNotReceiveWithAnyArgs().Issue(default!);
        await _unitOfWork.Received(1).SaveChangesAsync(CancellationToken.None);
        _metrics.Received(1).RecordStepUp(false);
    }

    [Fact]
    public async Task Wrong_code_counts_a_failed_sign_in_and_audits()
    {
        GivenMfa();
        var wrongCode = Totp.ComputeCode(_secret, Totp.TimeStepAt(Now) + 2);

        var result = await _sut.HandleAsync(new StepUpCommand(null, wrongCode, null), Ct);

        result.Error.ShouldBe(MfaErrors.InvalidCode);
        _user.AccessFailedCount.ShouldBe(1);
        _user.DomainEvents.ShouldBeEmpty();
        await _users.Received(1).RecordFailedSignInAsync(_user.Id, Now, 5, LockoutDuration, CancellationToken.None);
        await _methods.DidNotReceiveWithAnyArgs().TryClaimTimeStepAsync(default, default, default, Ct);
        AssertFailureAudit("auth.invalid_mfa_code");
        _auditEntries[0].Details.ShouldBe("""{"factor":"totp"}""");
        _session.AuthenticatedAt.ShouldBe(SignedInAt);
        await _unitOfWork.Received(1).SaveChangesAsync(CancellationToken.None);
        _metrics.Received(1).RecordStepUp(false);
    }

    [Fact]
    public async Task Lost_claim_is_a_wrong_code()
    {
        GivenMfa();
        _methods.TryClaimTimeStepAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.HandleAsync(new StepUpCommand(null, CurrentCode, null), Ct);

        result.Error.ShouldBe(MfaErrors.InvalidCode);
        _user.AccessFailedCount.ShouldBe(1);
        _session.AuthenticatedAt.ShouldBe(SignedInAt);
    }

    [Fact]
    public async Task Fifth_failure_locks_and_raises_the_lockout_event()
    {
        GivenMfa();
        FailBeforehand(times: 4);

        var result = await _sut.HandleAsync(new StepUpCommand(null, null, RecoveryCodeText), Ct);

        result.Error.ShouldBe(MfaErrors.InvalidCode);
        _user.AccessFailedCount.ShouldBe(5);
        _user.LockoutEnd.ShouldBe(Now + LockoutDuration);
        _user.DomainEvents.ShouldHaveSingleItem().ShouldBe(new UserLockedOutDomainEvent(_user.Id, Now + LockoutDuration));
        _auditEntries.Select(entry => entry.EventType).ShouldBe([AuthAuditEvents.StepUpFailed, AuthAuditEvents.LockedOut]);
        var lockedOut = _auditEntries[1];
        lockedOut.Succeeded.ShouldBeFalse();
        lockedOut.UserId.ShouldBe(_user.Id);
        lockedOut.SessionId.ShouldBe(_session.Id);
        lockedOut.Details.ShouldNotBeNull().ShouldContain("2026-10-17T09:15:10.5");
        await _unitOfWork.Received(1).SaveChangesAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Locked_account_is_refused_without_verifying(bool mfa)
    {
        if (mfa)
        {
            GivenMfa();
        }

        FailBeforehand(times: 5);
        var command = mfa ? new StepUpCommand(null, CurrentCode, null) : new StepUpCommand(Password, null, null);

        var result = await _sut.HandleAsync(command, Ct);

        result.Error.ShouldBe(mfa ? MfaErrors.InvalidCode : UserErrors.CurrentPasswordIncorrect);
        _passwordHasher.DidNotReceiveWithAnyArgs().Verify(default!, default!);
        await _methods.DidNotReceiveWithAnyArgs().GetTotpAsync(default, Ct);
        await _methods.DidNotReceiveWithAnyArgs().TryClaimTimeStepAsync(default, default, default, Ct);
        await _users.DidNotReceiveWithAnyArgs().RecordFailedSignInAsync(default, default, default, default, Ct);
        _user.AccessFailedCount.ShouldBe(5);
        _user.LockoutEnd.ShouldBe(Now + LockoutDuration - TimeSpan.FromMinutes(1));
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.StepUpFailed);
        audit.FailureReason.ShouldBe(StepUpCommandHandler.LockedFailureReason);
        _session.AuthenticatedAt.ShouldBe(SignedInAt);
        _accessTokenIssuer.DidNotReceiveWithAnyArgs().Issue(default!);
        _metrics.Received(1).RecordStepUp(false);
    }

    [Fact]
    public async Task Rehash_request_is_left_to_the_next_login()
    {
        var stamp = _user.SecurityStamp;
        _passwordHasher.Verify(StoredHash, Password).Returns(PasswordVerification.SuccessRehashNeeded);

        var result = await _sut.HandleAsync(new StepUpCommand(Password, null, null), Ct);

        result.IsSuccess.ShouldBeTrue();
        _user.PasswordHash.ShouldBe(StoredHash);
        _user.SecurityStamp.ShouldBe(stamp);
        _passwordHasher.DidNotReceiveWithAnyArgs().Hash(default!);
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("no session claim")]
    [InlineData("unknown user")]
    [InlineData("suspended")]
    [InlineData("unknown session")]
    [InlineData("another user's session")]
    [InlineData("revoked session")]
    [InlineData("stamp changed")]
    public async Task Caller_without_an_active_session_of_their_own_is_not_found(string state)
    {
        switch (state)
        {
            case "anonymous":
                _currentUser.UserId.Returns((Guid?)null);
                break;
            case "no session claim":
                _currentUser.SessionId.Returns((Guid?)null);
                break;
            case "unknown user":
                _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns((User?)null);
                break;
            case "suspended":
                _user.Suspend(SignedInAt);
                break;
            case "unknown session":
                _sessions.GetByIdAsync(_session.Id, Arg.Any<CancellationToken>()).Returns((UserSession?)null);
                break;
            case "another user's session":
                _currentUser.UserId.Returns(Guid.NewGuid());
                _users.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_user);
                break;
            case "revoked session":
                _session.Revoke(SessionRevokedReason.Logout, SignedInAt.AddMinutes(1));
                break;
            default:
                _user.ChangePassword("changed-hash", historyCount: 5, SignedInAt.AddMinutes(1));
                break;
        }

        var result = await _sut.HandleAsync(new StepUpCommand(Password, null, null), Ct);

        result.Error.Type.ShouldBe(ErrorType.NotFound);
        result.Error.Code.ShouldBe("auth.user_not_found");
        _passwordHasher.DidNotReceiveWithAnyArgs().Verify(default!, default!);
        await _users.DidNotReceiveWithAnyArgs().RecordFailedSignInAsync(default, default, default, default, Ct);
        _auditEntries.ShouldBeEmpty();
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
        _accessTokenIssuer.DidNotReceiveWithAnyArgs().Issue(default!);
        _metrics.DidNotReceiveWithAnyArgs().RecordStepUp(default);
    }

    [Fact]
    public async Task Session_past_its_absolute_expiry_is_not_found()
    {
        var (old, _) = UserSession.Start(
            _user.Id,
            "pwd",
            "Old",
            null,
            null,
            _user.SecurityStamp,
            [.. Enumerable.Range(1, 32).Select(value => (byte)value)],
            TimeSpan.FromDays(1),
            TimeSpan.FromDays(1),
            Now.AddDays(-1));
        _sessions.GetByIdAsync(old.Id, Arg.Any<CancellationToken>()).Returns(old);
        _currentUser.SessionId.Returns(old.Id);

        var result = await _sut.HandleAsync(new StepUpCommand(Password, null, null), Ct);

        result.Error.Type.ShouldBe(ErrorType.NotFound);
        old.AuthenticatedAt.ShouldBe(Now.AddDays(-1));
    }

    [Theory]
    [InlineData(129)]
    [InlineData(1024 * 1024)]
    public async Task Oversized_password_is_rejected_before_any_lookup(int length)
    {
        var pipeline = new ValidationDecorator.CommandHandler<StepUpCommand, StepUpResponse>(
            _sut,
            [new StepUpCommandValidator(Options.Create(new PasswordOptions()))]);

        var result = await pipeline.HandleAsync(new StepUpCommand(new string('p', length), null, null), Ct);

        result.Error.ShouldBeOfType<ValidationError>().Errors.Keys.ShouldContain("password");
        await _users.DidNotReceiveWithAnyArgs().GetByIdAsync(default, Ct);
        _passwordHasher.DidNotReceiveWithAnyArgs().Verify(default!, default!);
    }

    [Fact]
    public void Response_text_never_shows_the_token()
    {
        new StepUpResponse(AccessTokenValue, Now, Now).ToString().ShouldBe(nameof(StepUpResponse));
    }

    private void GivenMfa()
    {
        _methods.HasConfirmedAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(true);
        var method = UserMfaMethod.StartTotp(_user.Id, "Phone", ProtectedSecret, SignedInAt.AddDays(-1));
        method.Confirm(Totp.TimeStepAt(SignedInAt.AddDays(-1)), SignedInAt.AddDays(-1)).IsSuccess.ShouldBeTrue();
        _methods.GetTotpAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(method);
        _secretProtector.Unprotect(ProtectedSecret).Returns(_ => _secret.ToArray());
    }

    // Failures counted a minute ago, so a lockout they cause ends a minute before Now + LockoutDuration.
    private void FailBeforehand(int times)
    {
        for (var attempt = 0; attempt < times; attempt++)
        {
            _user.RecordFailedSignIn(Now.AddMinutes(-1), maxFailedAttempts: 5, LockoutDuration);
        }

        _user.ClearDomainEvents();
    }

    private void AssertFailureAudit(string failureReason)
    {
        var audit = _auditEntries[0];
        audit.EventType.ShouldBe(AuthAuditEvents.StepUpFailed);
        audit.Succeeded.ShouldBeFalse();
        audit.UserId.ShouldBe(_user.Id);
        audit.SessionId.ShouldBe(_session.Id);
        audit.FailureReason.ShouldBe(failureReason);
    }
}
