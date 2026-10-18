using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Authentication;
using TemplateName.Modules.Auth.Application.Authentication.Login;
using TemplateName.Modules.Auth.Application.Mfa;
using TemplateName.Modules.Auth.Application.Passwords;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Users.Events;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Auth;

public sealed class LoginCommandHandlerTests
{
    private const string Password = "correct horse battery";
    private const string StoredHash = "stored-hash";
    private const string RefreshTokenValue = "refresh-token-value-of-forty-three-chars-xx";
    private const string AccessTokenValue = "header.payload.signature";

    private static readonly DateTimeOffset Now = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);
    private static readonly byte[] RefreshTokenHash = [.. Enumerable.Range(1, 32).Select(value => (byte)value)];
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IMfaMethodRepository _mfaMethods = Substitute.For<IMfaMethodRepository>();
    private readonly IMfaChallengeRepository _challenges = Substitute.For<IMfaChallengeRepository>();
    private readonly ISessionRepository _sessions = Substitute.For<ISessionRepository>();
    private readonly IPasswordHasher _passwordHasher = Substitute.For<IPasswordHasher>();
    private readonly ISecureTokenService _tokenService = Substitute.For<ISecureTokenService>();
    private readonly IAccessTokenIssuer _accessTokenIssuer = Substitute.For<IAccessTokenIssuer>();
    private readonly IAuthAuditWriter _auditWriter = Substitute.For<IAuthAuditWriter>();
    private readonly IClientContext _clientContext = Substitute.For<IClientContext>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IAuthMetrics _metrics = Substitute.For<IAuthMetrics>();
    private readonly List<AuthAuditLog> _auditEntries = [];
    private readonly List<UserSession> _addedSessions = [];
    private readonly List<MfaChallenge> _addedChallenges = [];
    private readonly List<AccessTokenRequest> _tokenRequests = [];
    private readonly LoginCommandHandler _sut;

    public LoginCommandHandlerTests()
    {
        _tokenService.Generate().Returns(new GeneratedToken(RefreshTokenValue, RefreshTokenHash));
        _accessTokenIssuer.Issue(Arg.Do<AccessTokenRequest>(_tokenRequests.Add)).Returns(new AccessToken(AccessTokenValue, Now.AddMinutes(10)));
        _clientContext.IpAddress.Returns("203.0.113.7");
        _clientContext.UserAgent.Returns("UnitTests/1.0");
        _auditWriter.Record(Arg.Do<AuthAuditLog>(_auditEntries.Add));
        _sessions.Add(Arg.Do<UserSession>(_addedSessions.Add));
        _challenges.Add(Arg.Do<MfaChallenge>(_addedChallenges.Add));
        _unitOfWork.TrySaveChangesAsync(Arg.Any<CancellationToken>()).Returns(true);

        var sessionStarter = new SessionStarter(_sessions, _tokenService, _accessTokenIssuer, _clientContext, Options.Create(new RefreshTokenOptions()));
        _sut = new LoginCommandHandler(
            _users,
            _mfaMethods,
            _challenges,
            _passwordHasher,
            _tokenService,
            sessionStarter,
            _auditWriter,
            _unitOfWork,
            _metrics,
            Options.Create(new LockoutOptions()),
            Options.Create(new MfaOptions()),
            new FakeTimeProvider(Now));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Unknown_email_still_spends_verification_cost()
    {
        var result = await _sut.HandleAsync(Command() with { Email = " Alice@Example.com " }, Ct);

        result.Error.ShouldBe(UserErrors.InvalidCredentials);
        _passwordHasher.Received(1).SpendVerificationCost(Password);
        _passwordHasher.DidNotReceiveWithAnyArgs().Verify(default!, default!);
        await _users.Received(1).GetByNormalizedEmailAsync("ALICE@EXAMPLE.COM", Arg.Any<CancellationToken>());
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.LoginFailed);
        audit.Succeeded.ShouldBeFalse();
        audit.UserId.ShouldBeNull();
        audit.FailureReason.ShouldBe("auth.invalid_credentials");
        audit.AttemptedIdentifier.ShouldBe("A****@Example.com");
        await _unitOfWork.Received(1).SaveChangesAsync(Ct);
        await _unitOfWork.DidNotReceiveWithAnyArgs().TrySaveChangesAsync(Ct);
        await _users.DidNotReceiveWithAnyArgs().RecordFailedSignInAsync(default, default, default, default, Ct);
        _sessions.DidNotReceiveWithAnyArgs().Add(default!);
        _metrics.Received(1).RecordLogin(LoginOutcome.InvalidCredentials);
    }

    [Fact]
    public async Task User_without_password_hash_is_treated_like_an_unknown_email()
    {
        var user = GivenUser(passwordHash: null);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(UserErrors.InvalidCredentials);
        _passwordHasher.Received(1).SpendVerificationCost(Password);
        _passwordHasher.DidNotReceiveWithAnyArgs().Verify(default!, default!);
        user.AccessFailedCount.ShouldBe(0);
        user.LockoutEnd.ShouldBeNull();
        await _users.DidNotReceiveWithAnyArgs().RecordFailedSignInAsync(default, default, default, default, Ct);
        _auditEntries.ShouldHaveSingleItem().FailureReason.ShouldBe("auth.invalid_credentials");
        _metrics.Received(1).RecordLogin(LoginOutcome.InvalidCredentials);
    }

    [Fact]
    public async Task Wrong_password_increments_failures_and_saves()
    {
        var user = GivenUser();
        _passwordHasher.Verify(StoredHash, Password).Returns(PasswordVerification.Failed);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(UserErrors.InvalidCredentials);
        user.AccessFailedCount.ShouldBe(1);
        user.LockoutEnd.ShouldBeNull();
        await _users.Received(1).RecordFailedSignInAsync(user.Id, Now, 5, LockoutDuration, Ct);
        user.DomainEvents.ShouldBeEmpty();
        _passwordHasher.DidNotReceiveWithAnyArgs().SpendVerificationCost(default!);
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.LoginFailed);
        audit.UserId.ShouldBe(user.Id);
        audit.FailureReason.ShouldBe("auth.invalid_credentials");
        audit.AttemptedIdentifier.ShouldBe("a****@example.com");
        await _unitOfWork.Received(1).SaveChangesAsync(Ct);
        _sessions.DidNotReceiveWithAnyArgs().Add(default!);
        _accessTokenIssuer.DidNotReceiveWithAnyArgs().Issue(default!);
        _metrics.Received(1).RecordLogin(LoginOutcome.InvalidCredentials);
    }

    [Fact]
    public async Task Fifth_failure_locks_and_audits_lockout()
    {
        var user = GivenUser();
        FailBeforehand(user, times: 4);
        _passwordHasher.Verify(StoredHash, Password).Returns(PasswordVerification.Failed);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(UserErrors.InvalidCredentials);
        user.AccessFailedCount.ShouldBe(5);
        user.LockoutEnd.ShouldBe(Now + LockoutDuration);
        user.DomainEvents.ShouldHaveSingleItem().ShouldBe(new UserLockedOutDomainEvent(user.Id, Now + LockoutDuration));
        _auditEntries.Select(entry => entry.EventType).ShouldBe([AuthAuditEvents.LoginFailed, AuthAuditEvents.LockedOut]);
        var lockedOut = _auditEntries[1];
        lockedOut.UserId.ShouldBe(user.Id);
        lockedOut.AttemptedIdentifier.ShouldBe("a****@example.com");
        lockedOut.Details.ShouldNotBeNull().ShouldContain("2026-10-08T09:15:00");
        await _unitOfWork.Received(1).SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Wrong_password_while_locked_changes_no_counter()
    {
        var user = GivenUser();
        FailBeforehand(user, times: 5);
        _passwordHasher.Verify(StoredHash, Password).Returns(PasswordVerification.Failed);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(UserErrors.InvalidCredentials);
        user.AccessFailedCount.ShouldBe(5);
        user.LockoutEnd.ShouldBe(Now + LockoutDuration);
        _auditEntries.ShouldHaveSingleItem().EventType.ShouldBe(AuthAuditEvents.LoginFailed);
    }

    [Fact]
    public async Task Correct_password_while_locked_returns_invalid_credentials_and_leaves_counters()
    {
        var user = GivenUser();
        FailBeforehand(user, times: 5);
        _passwordHasher.Verify(StoredHash, Password).Returns(PasswordVerification.Success);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(UserErrors.InvalidCredentials);
        user.AccessFailedCount.ShouldBe(5);
        user.LockoutEnd.ShouldBe(Now + LockoutDuration);
        user.LastLoginAt.ShouldBeNull();
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.LoginFailed);
        audit.FailureReason.ShouldBe(LoginCommandHandler.LockedFailureReason);
        audit.UserId.ShouldBe(user.Id);
        await _unitOfWork.Received(1).SaveChangesAsync(Ct);
        _sessions.DidNotReceiveWithAnyArgs().Add(default!);
        _accessTokenIssuer.DidNotReceiveWithAnyArgs().Issue(default!);
        _metrics.Received(1).RecordLogin(LoginOutcome.Locked);
    }

    [Fact]
    public async Task Unverified_email_returns_403_only_after_the_password_is_correct()
    {
        var user = GivenUser(confirmed: false);
        _passwordHasher.Verify(StoredHash, "wrong password").Returns(PasswordVerification.Failed);
        _passwordHasher.Verify(StoredHash, Password).Returns(PasswordVerification.Success);

        var wrong = await _sut.HandleAsync(Command() with { Password = "wrong password" }, Ct);
        var correct = await _sut.HandleAsync(Command(), Ct);

        wrong.Error.ShouldBe(UserErrors.InvalidCredentials);
        correct.Error.ShouldBe(UserErrors.EmailNotVerified);
        correct.Error.Type.ShouldBe(ErrorType.Forbidden);
        user.AccessFailedCount.ShouldBe(1);
        user.LastLoginAt.ShouldBeNull();
        _auditEntries.Select(entry => entry.FailureReason).ShouldBe(["auth.invalid_credentials", "auth.email_not_verified"]);
        _auditEntries.ShouldAllBe(entry => entry.EventType == AuthAuditEvents.LoginFailed && !entry.Succeeded);
        await _unitOfWork.Received(2).SaveChangesAsync(Ct);
        _sessions.DidNotReceiveWithAnyArgs().Add(default!);
        _metrics.Received(1).RecordLogin(LoginOutcome.InvalidCredentials);
        _metrics.Received(1).RecordLogin(LoginOutcome.Unverified);
    }

    [Fact]
    public async Task Suspended_user_with_the_correct_password_returns_account_inactive()
    {
        var user = GivenUser();
        user.Suspend(Now);
        _passwordHasher.Verify(StoredHash, Password).Returns(PasswordVerification.Success);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(UserErrors.AccountInactive);
        _auditEntries.ShouldHaveSingleItem().FailureReason.ShouldBe("auth.account_inactive");
        _sessions.DidNotReceiveWithAnyArgs().Add(default!);
        _metrics.Received(1).RecordLogin(LoginOutcome.Inactive);
    }

    [Fact]
    public async Task Success_starts_a_session_issues_both_tokens_and_audits()
    {
        var user = GivenUser();
        FailBeforehand(user, times: 2);
        _passwordHasher.Verify(StoredHash, Password).Returns(PasswordVerification.Success);

        var result = await _sut.HandleAsync(Command() with { DeviceName = "Alice's phone" }, Ct);

        var response = result.Value;
        var session = _addedSessions.ShouldHaveSingleItem();
        response.ShouldBe(LoginResult.Signed(new LoginResponse(AccessTokenValue, Now.AddMinutes(10), RefreshTokenValue, Now.AddDays(14), session.Id)));
        session.UserId.ShouldBe(user.Id);
        session.AuthMethods.ShouldBe("pwd");
        session.DeviceName.ShouldBe("Alice's phone");
        session.UserAgent.ShouldBe("UnitTests/1.0");
        session.IpAddress.ShouldBe("203.0.113.7");
        session.SecurityStamp.ShouldBe(user.SecurityStamp);
        session.ExpiresAt.ShouldBe(Now.AddDays(90));
        session.RefreshTokens.ShouldHaveSingleItem().TokenHash.ShouldBe(RefreshTokenHash);
        _tokenRequests.ShouldHaveSingleItem().ShouldBe(new AccessTokenRequest(user.Id, session.Id, user.SecurityStamp, "pwd", Now, "ms"));

        user.AccessFailedCount.ShouldBe(0);
        user.LastLoginAt.ShouldBe(Now);
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.LoginSucceeded);
        audit.Succeeded.ShouldBeTrue();
        audit.UserId.ShouldBe(user.Id);
        audit.SessionId.ShouldBe(session.Id);
        await _unitOfWork.Received(1).TrySaveChangesAsync(Ct);
        _metrics.Received(1).RecordLogin(LoginOutcome.Succeeded);
        _passwordHasher.DidNotReceiveWithAnyArgs().Hash(default!);
    }

    [Fact]
    public async Task Device_name_defaults_to_Unknown()
    {
        GivenUser();
        _passwordHasher.Verify(StoredHash, Password).Returns(PasswordVerification.Success);

        await _sut.HandleAsync(Command(), Ct);

        _addedSessions.ShouldHaveSingleItem().DeviceName.ShouldBe("Unknown");
    }

    [Fact]
    public async Task Rehash_is_stored_when_the_verifier_requests_it()
    {
        var user = GivenUser();
        var stamp = user.SecurityStamp;
        _passwordHasher.Verify(StoredHash, Password).Returns(PasswordVerification.SuccessRehashNeeded);
        _passwordHasher.Hash(Password).Returns("stronger-hash");

        var result = await _sut.HandleAsync(Command(), Ct);

        result.IsSuccess.ShouldBeTrue();
        user.PasswordHash.ShouldBe("stronger-hash");
        user.SecurityStamp.ShouldBe(stamp);
        user.PasswordHistory.ShouldHaveSingleItem().PasswordHash.ShouldBe(StoredHash);
        user.DomainEvents.ShouldNotContain(domainEvent => domainEvent is PasswordChangedDomainEvent);
        _addedSessions.ShouldHaveSingleItem().SecurityStamp.ShouldBe(stamp);
        await _unitOfWork.Received(1).TrySaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Concurrency_conflict_on_save_returns_invalid_credentials()
    {
        GivenUser();
        _passwordHasher.Verify(StoredHash, Password).Returns(PasswordVerification.Success);
        _unitOfWork.TrySaveChangesAsync(Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.HandleAsync(Command(), Ct);

        // Another request changed the user first: answer like a wrong password, never with a 409 that only a known account can get.
        result.Error.ShouldBe(UserErrors.InvalidCredentials);
        _metrics.Received(1).RecordLogin(LoginOutcome.InvalidCredentials);
        _metrics.DidNotReceive().RecordLogin(LoginOutcome.Succeeded);
    }

    [Fact]
    public async Task Mfa_account_gets_a_challenge_and_no_session()
    {
        var user = GivenUser();
        GivenMfa(user);
        _passwordHasher.Verify(StoredHash, Password).Returns(PasswordVerification.Success);

        var result = await _sut.HandleAsync(Command() with { DeviceName = "Alice's phone" }, Ct);

        // The challenge token is a fresh secure token, shown once; only its hash is stored, bound to the user's stamp for five minutes.
        var response = result.Value;
        response.MfaRequired.ShouldBeTrue();
        response.ChallengeToken.ShouldBe(RefreshTokenValue);
        response.ChallengeExpiresAt.ShouldBe(Now.AddMinutes(5));
        response.Methods.ShouldBe(["totp", "recovery_code"]);
        response.AccessToken.ShouldBeNull();
        response.AccessTokenExpiresAt.ShouldBeNull();
        response.RefreshToken.ShouldBeNull();
        response.RefreshTokenExpiresAt.ShouldBeNull();
        response.SessionId.ShouldBeNull();
        var challenge = _addedChallenges.ShouldHaveSingleItem();
        challenge.UserId.ShouldBe(user.Id);
        challenge.TokenHash.ShouldBe(RefreshTokenHash);
        challenge.SecurityStamp.ShouldBe(user.SecurityStamp);
        challenge.DeviceName.ShouldBe("Alice's phone");
        challenge.CreatedAt.ShouldBe(Now);
        challenge.ExpiresAt.ShouldBe(Now.AddMinutes(5));
        challenge.FailedAttempts.ShouldBe(0);

        _sessions.DidNotReceiveWithAnyArgs().Add(default!);
        _accessTokenIssuer.DidNotReceiveWithAnyArgs().Issue(default!);
        user.LastLoginAt.ShouldBeNull();
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.MfaChallengeIssued);
        audit.Succeeded.ShouldBeTrue();
        audit.UserId.ShouldBe(user.Id);
        audit.AttemptedIdentifier.ShouldBe("a****@example.com");
        audit.SessionId.ShouldBeNull();
        await _unitOfWork.Received(1).TrySaveChangesAsync(Ct);
        _metrics.Received(1).RecordLogin(LoginOutcome.MfaRequired);
        _metrics.DidNotReceive().RecordLogin(LoginOutcome.Succeeded);
    }

    [Fact]
    public async Task Password_step_does_not_reset_the_failure_count_of_an_mfa_account()
    {
        var user = GivenUser();
        GivenMfa(user);
        FailBeforehand(user, times: 2);
        _passwordHasher.Verify(StoredHash, Password).Returns(PasswordVerification.Success);

        var result = await _sut.HandleAsync(Command(), Ct);

        // Only a verified second factor resets the count (decision D7), or "password, four wrong codes, password again" would never end.
        result.Value.MfaRequired.ShouldBeTrue();
        user.AccessFailedCount.ShouldBe(2);
        user.LockoutEnd.ShouldBeNull();
        user.LastLoginAt.ShouldBeNull();
        await _users.DidNotReceiveWithAnyArgs().RecordFailedSignInAsync(default, default, default, default, Ct);
    }

    [Theory]
    [InlineData("unknown email")]
    [InlineData("wrong password")]
    [InlineData("no password")]
    [InlineData("locked")]
    [InlineData("unverified")]
    [InlineData("suspended")]
    public async Task Mfa_lookup_happens_only_after_a_correct_password(string state)
    {
        var user = GivenUser(passwordHash: state == "no password" ? null : StoredHash, confirmed: state != "unverified");
        GivenMfa(user);
        _passwordHasher.Verify(StoredHash, Password).Returns(state == "wrong password" ? PasswordVerification.Failed : PasswordVerification.Success);
        var command = Command();
        switch (state)
        {
            case "unknown email":
                command = command with { Email = "nobody@example.com" };
                break;
            case "locked":
                FailBeforehand(user, times: 5);
                break;
            case "suspended":
                user.Suspend(Now);
                break;
        }

        var result = await _sut.HandleAsync(command, Ct);

        // Nothing two-step is read or decided before the password is right and the account may sign in, so these answers stay Plan 2's.
        result.IsFailure.ShouldBeTrue();
        await _mfaMethods.DidNotReceiveWithAnyArgs().HasConfirmedAsync(default, Ct);
        _challenges.DidNotReceiveWithAnyArgs().Add(default!);
        _tokenService.DidNotReceiveWithAnyArgs().Generate();
        _auditEntries.ShouldAllBe(entry => entry.EventType != AuthAuditEvents.MfaChallengeIssued);
    }

    [Fact]
    public async Task Non_mfa_account_signs_in_as_before()
    {
        var user = GivenUser();
        _passwordHasher.Verify(StoredHash, Password).Returns(PasswordVerification.Success);

        var result = await _sut.HandleAsync(Command(), Ct);

        var session = _addedSessions.ShouldHaveSingleItem();
        result.Value.ShouldBe(LoginResult.Signed(new LoginResponse(AccessTokenValue, Now.AddMinutes(10), RefreshTokenValue, Now.AddDays(14), session.Id)));
        result.Value.MfaRequired.ShouldBeFalse();
        result.Value.ChallengeToken.ShouldBeNull();
        result.Value.ChallengeExpiresAt.ShouldBeNull();
        result.Value.Methods.ShouldBeNull();
        await _mfaMethods.Received(1).HasConfirmedAsync(user.Id, Ct);
        _challenges.DidNotReceiveWithAnyArgs().Add(default!);
        session.AuthMethods.ShouldBe("pwd");
        _auditEntries.ShouldHaveSingleItem().EventType.ShouldBe(AuthAuditEvents.LoginSucceeded);
        _metrics.Received(1).RecordLogin(LoginOutcome.Succeeded);
    }

    [Fact]
    public async Task Lost_save_race_of_a_challenge_returns_invalid_credentials()
    {
        var user = GivenUser();
        GivenMfa(user);
        _passwordHasher.Verify(StoredHash, Password).Returns(PasswordVerification.SuccessRehashNeeded);
        _passwordHasher.Hash(Password).Returns("stronger-hash");
        _unitOfWork.TrySaveChangesAsync(Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.HandleAsync(Command(), Ct);

        // The rehash makes the challenge's save write the user row, so it can lose a race; the answer is the generic 401, as on Plan 2's path.
        result.Error.ShouldBe(UserErrors.InvalidCredentials);
        user.PasswordHash.ShouldBe("stronger-hash");
        _metrics.Received(1).RecordLogin(LoginOutcome.InvalidCredentials);
        _metrics.DidNotReceive().RecordLogin(LoginOutcome.MfaRequired);
    }

    [Fact]
    public void Login_result_text_shows_only_whether_mfa_is_required()
    {
        LoginResult.Challenge("challenge-token-value", Now).ToString().ShouldBe("LoginResult { MfaRequired = True }");
        LoginResult.Signed(new LoginResponse(AccessTokenValue, Now, RefreshTokenValue, Now, Guid.Empty)).ToString()
            .ShouldBe("LoginResult { MfaRequired = False }");
    }

    [Theory]
    [InlineData(129)]
    [InlineData(1024 * 1024)]
    public async Task Oversized_password_is_rejected_before_hashing(int length)
    {
        // The pipeline the host builds: the validation decorator runs the validator before the handler.
        var pipeline = new ValidationDecorator.CommandHandler<LoginCommand, LoginResult>(
            _sut,
            [new LoginCommandValidator(Options.Create(new PasswordOptions()))]);

        var result = await pipeline.HandleAsync(Command() with { Password = new string('p', length) }, Ct);

        result.Error.ShouldBeOfType<ValidationError>().Errors.Keys.ShouldContain("password");
        _passwordHasher.DidNotReceiveWithAnyArgs().Verify(default!, default!);
        _passwordHasher.DidNotReceiveWithAnyArgs().SpendVerificationCost(default!);
        await _users.DidNotReceiveWithAnyArgs().GetByNormalizedEmailAsync(default!, Ct);
        await _unitOfWork.DidNotReceiveWithAnyArgs().TrySaveChangesAsync(Ct);
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public void Command_text_never_shows_the_password()
    {
        Command().ToString().ShouldNotContain(Password);
    }

    private static LoginCommand Command() => new("alice@example.com", Password, null);

    private void GivenMfa(User user) => _mfaMethods.HasConfirmedAsync(user.Id, Arg.Any<CancellationToken>()).Returns(true);

    private static void FailBeforehand(User user, int times)
    {
        for (var attempt = 0; attempt < times; attempt++)
        {
            user.RecordFailedSignIn(Now, maxFailedAttempts: 5, LockoutDuration);
        }

        user.ClearDomainEvents();
    }

    private User GivenUser(string? passwordHash = StoredHash, bool confirmed = true)
    {
        var user = User.Register("alice@example.com", "Alice", "ms", passwordHash, Now.AddDays(-1)).Value;
        if (confirmed)
        {
            user.ConfirmEmail(Now.AddDays(-1));
        }

        user.ClearDomainEvents();
        _users.GetByNormalizedEmailAsync("ALICE@EXAMPLE.COM", Arg.Any<CancellationToken>()).Returns(user);

        // The database counts atomically; User.RecordFailedSignIn is the specification of that count (AuthPersistenceTests runs both
        // over the same states). The fake applies it to the test's instance, as if the row were read back, and keeps none of its events:
        // the handler must raise the lockout event itself.
        _users.RecordFailedSignInAsync(user.Id, Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var now = call.ArgAt<DateTimeOffset>(1);
                if (user.IsLockedOut(now))
                {
                    return (FailedSignIn?)null;
                }

                user.RecordFailedSignIn(now, call.ArgAt<int>(2), call.ArgAt<TimeSpan>(3));
                user.ClearDomainEvents();
                return new FailedSignIn(user.AccessFailedCount, user.LockoutEnd);
            });
        return user;
    }
}
