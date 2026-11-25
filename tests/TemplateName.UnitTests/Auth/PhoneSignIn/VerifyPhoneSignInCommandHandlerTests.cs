using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Authentication;
using TemplateName.Modules.Auth.Application.Mfa;
using TemplateName.Modules.Auth.Application.Phone;
using TemplateName.Modules.Auth.Application.PhoneSignIn;
using TemplateName.Modules.Auth.Application.PhoneSignIn.Verify;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Users.Events;
using TemplateName.Modules.Auth.Domain.Verification;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Auth.PhoneSignIn;

public sealed class VerifyPhoneSignInCommandHandlerTests
{
    private const string Number = "+60123456789";
    private const string Code = "042137";
    private const string WrongCode = "999999";
    private const string AccessTokenValue = "header.payload.signature";

    private static readonly DateTimeOffset Now = new(2026, 11, 24, 8, 0, 0, TimeSpan.Zero);
    private static readonly byte[] TokenHash = [.. Enumerable.Range(1, 32).Select(value => (byte)value)];

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IPhoneVerificationCodeRepository _codes = Substitute.For<IPhoneVerificationCodeRepository>();
    private readonly IRoleRepository _roles = Substitute.For<IRoleRepository>();
    private readonly IMfaMethodRepository _mfaMethods = Substitute.For<IMfaMethodRepository>();
    private readonly IMfaChallengeRepository _challenges = Substitute.For<IMfaChallengeRepository>();
    private readonly ISessionRepository _sessions = Substitute.For<ISessionRepository>();
    private readonly ISecureTokenService _tokenService = Substitute.For<ISecureTokenService>();
    private readonly IAccessTokenIssuer _accessTokenIssuer = Substitute.For<IAccessTokenIssuer>();
    private readonly IClientContext _clientContext = Substitute.For<IClientContext>();
    private readonly IAuthAuditWriter _auditWriter = Substitute.For<IAuthAuditWriter>();
    private readonly IAuthMetrics _metrics = Substitute.For<IAuthMetrics>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly List<AuthAuditLog> _auditEntries = [];
    private readonly List<UserSession> _addedSessions = [];
    private readonly List<MfaChallenge> _addedChallenges = [];
    private readonly User _user = User.Register("alice@example.com", "Alice", "en", "hash", Now.AddDays(-30)).Value;
    private readonly PhoneOptions _options = new() { Enabled = true, SignInEnabled = true };
    private readonly PhoneVerificationCode _code;
    private readonly VerifyPhoneSignInCommandHandler _sut;

    public VerifyPhoneSignInCommandHandlerTests()
    {
        _user.VerifyPhoneNumber(Number, Now.AddDays(-2));
        _user.ClearDomainEvents();
        _code = PhoneVerificationCode.Issue(_user.Id, PhoneCodePurpose.SignIn, Number, Code, "protected", TimeSpan.FromMinutes(5), Now.AddMinutes(-1));
        _users.GetByPhoneNumberAsync(Number, Arg.Any<CancellationToken>()).Returns(_user);
        _codes.GetPendingAsync(_user.Id, PhoneCodePurpose.SignIn, Now, Arg.Any<CancellationToken>()).Returns(_code);
        _codes.TryConsumeAsync(_code.Id, Now, Arg.Any<CancellationToken>()).Returns(true);
        _codes.TryClaimAttemptAsync(_code.Id, 5, Now, Arg.Any<CancellationToken>()).Returns(true);
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
        _tokenService.Generate().Returns(new GeneratedToken("token-value-of-forty-three-characters-xxxx", TokenHash));
        _accessTokenIssuer.Issue(Arg.Any<AccessTokenRequest>()).Returns(new AccessToken(AccessTokenValue, Now.AddMinutes(10)));
        _auditWriter.Record(Arg.Do<AuthAuditLog>(_auditEntries.Add));
        _sessions.Add(Arg.Do<UserSession>(_addedSessions.Add));
        _challenges.Add(Arg.Do<MfaChallenge>(_addedChallenges.Add));
        _unitOfWork.TrySaveChangesAsync(Arg.Any<CancellationToken>()).Returns(true);
        var options = Options.Create(_options);
        _sut = new VerifyPhoneSignInCommandHandler(
            _users,
            _codes,
            _mfaMethods,
            _challenges,
            _tokenService,
            new SessionStarter(_sessions, _tokenService, _accessTokenIssuer, _clientContext, Options.Create(new RefreshTokenOptions())),
            new PhoneSignInGate(_roles, _mfaMethods, options),
            _auditWriter,
            _metrics,
            _unitOfWork,
            options,
            Options.Create(new LockoutOptions()),
            Options.Create(new MfaOptions()),
            new FakeTimeProvider(Now));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Right_code_signs_in_with_amr_sms()
    {
        _user.RecordFailedSignIn(Now.AddMinutes(-5), maxFailedAttempts: 5, TimeSpan.FromMinutes(15));

        var result = await _sut.HandleAsync(Command(), Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.MfaRequired.ShouldBeFalse();
        result.Value.AccessToken.ShouldBe(AccessTokenValue);
        var session = _addedSessions.ShouldHaveSingleItem();
        session.AuthMethods.ShouldBe("sms");
        session.DeviceName.ShouldBe("Pixel 9");
        _user.AccessFailedCount.ShouldBe(0);
        _code.ConsumedAt.ShouldBe(Now);
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.LoginSucceeded);
        audit.Details.ShouldBe("""{"method":"sms"}""");
        audit.SessionId.ShouldBe(session.Id);
        _metrics.Received(1).RecordPhoneSignIn(PhoneSignInOutcome.SignedIn);
    }

    [Fact]
    public async Task Mfa_account_gets_a_challenge_with_first_factor_sms()
    {
        _mfaMethods.HasConfirmedAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(true);
        _user.RecordFailedSignIn(Now.AddMinutes(-5), maxFailedAttempts: 5, TimeSpan.FromMinutes(15));

        var result = await _sut.HandleAsync(Command(), Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.MfaRequired.ShouldBeTrue();
        result.Value.ChallengeToken.ShouldNotBeNull();
        _addedChallenges.ShouldHaveSingleItem().FirstFactor.ShouldBe(AuthenticationMethods.Sms);
        _addedSessions.ShouldBeEmpty();
        _user.AccessFailedCount.ShouldBe(1);
        _auditEntries.ShouldHaveSingleItem().EventType.ShouldBe(AuthAuditEvents.MfaChallengeIssued);
        _metrics.Received(1).RecordPhoneSignIn(PhoneSignInOutcome.Challenged);
    }

    [Fact]
    public async Task Wrong_code_counts_on_the_code_and_on_the_account()
    {
        var result = await _sut.HandleAsync(Command(code: WrongCode), Ct);

        result.Error.ShouldBe(PhoneErrors.InvalidCode);
        await _codes.Received(1).TryClaimAttemptAsync(_code.Id, 5, Now, CancellationToken.None);
        await _users.Received(1).RecordFailedSignInAsync(_user.Id, Now, 5, TimeSpan.FromMinutes(15), CancellationToken.None);
        _user.AccessFailedCount.ShouldBe(1);
        _code.ConsumedAt.ShouldBeNull();
        AssertFailedAudit("wrong_code", _user.Id);
        _addedSessions.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_guess_is_claimed_before_the_code_is_compared()
    {
        // The claim is the per-code limit: when the code's tries are used up (also by requests running at the same moment), even the
        // right code is not compared, so parallel guesses never get more than MaxAttempts comparisons.
        _codes.TryClaimAttemptAsync(_code.Id, 5, Now, Arg.Any<CancellationToken>()).Returns(false);

        (await _sut.HandleAsync(Command(), Ct)).Error.ShouldBe(PhoneErrors.InvalidCode);

        await _codes.DidNotReceiveWithAnyArgs().TryConsumeAsync(default, default, Ct);
        await _users.DidNotReceiveWithAnyArgs().RecordFailedSignInAsync(default, default, default, default, Ct);
        _addedSessions.ShouldBeEmpty();
        AssertFailedAudit("no_code", _user.Id);
    }

    [Fact]
    public async Task The_failure_that_locks_raises_the_lockout_event()
    {
        for (var i = 0; i < 4; i++)
        {
            _user.RecordFailedSignIn(Now.AddMinutes(-1), maxFailedAttempts: 5, TimeSpan.FromMinutes(15));
        }

        await _sut.HandleAsync(Command(code: WrongCode), Ct);

        _user.IsLockedOut(Now).ShouldBeTrue();
        _user.DomainEvents.OfType<UserLockedOutDomainEvent>().ShouldHaveSingleItem();
        _auditEntries.ShouldContain(entry => entry.EventType == AuthAuditEvents.LockedOut);
    }

    [Fact]
    public async Task No_pending_code_counts_nothing()
    {
        _codes.GetPendingAsync(_user.Id, PhoneCodePurpose.SignIn, Now, Arg.Any<CancellationToken>()).Returns((PhoneVerificationCode?)null);

        (await _sut.HandleAsync(Command(), Ct)).Error.ShouldBe(PhoneErrors.InvalidCode);

        await _users.DidNotReceiveWithAnyArgs().RecordFailedSignInAsync(default, default, default, default, Ct);
        AssertFailedAudit("no_code", _user.Id);
    }

    [Fact]
    public async Task Unknown_number_is_invalid_code()
    {
        _users.GetByPhoneNumberAsync(Number, Arg.Any<CancellationToken>()).Returns((User?)null);

        (await _sut.HandleAsync(Command(), Ct)).Error.ShouldBe(PhoneErrors.InvalidCode);

        AssertFailedAudit("no_code", userId: null);
        _metrics.Received(1).RecordPhoneSignIn(PhoneSignInOutcome.Refused);
    }

    [Theory]
    [InlineData("0123456789")]
    [InlineData("+60321234567")]
    public async Task Invalid_number_is_invalid_code_not_invalid_phone_number(string number)
    {
        (await _sut.HandleAsync(Command(number: number), Ct)).Error.ShouldBe(PhoneErrors.InvalidCode);

        await _users.DidNotReceiveWithAnyArgs().GetByPhoneNumberAsync(default!, Ct);
    }

    [Fact]
    public async Task Locked_account_is_invalid_code_and_spends_nothing()
    {
        _user.RecordFailedSignIn(Now.AddMinutes(-1), maxFailedAttempts: 1, TimeSpan.FromMinutes(15));

        (await _sut.HandleAsync(Command(), Ct)).Error.ShouldBe(PhoneErrors.InvalidCode);

        await _codes.DidNotReceiveWithAnyArgs().TryConsumeAsync(default, default, Ct);
        await _codes.DidNotReceiveWithAnyArgs().TryClaimAttemptAsync(default, default, default, Ct);
        await _users.DidNotReceiveWithAnyArgs().RecordFailedSignInAsync(default, default, default, default, Ct);
        AssertFailedAudit("locked", _user.Id);
    }

    [Fact]
    public async Task Suspended_after_the_right_code_is_account_inactive()
    {
        _user.Suspend(Now.AddDays(-1));

        (await _sut.HandleAsync(Command(), Ct)).Error.ShouldBe(UserErrors.AccountInactive);

        _code.ConsumedAt.ShouldBe(Now);
        _addedSessions.ShouldBeEmpty();
        AssertFailedAudit("inactive", _user.Id);
    }

    [Fact]
    public async Task Suspended_with_a_wrong_code_is_invalid_code()
    {
        _user.Suspend(Now.AddDays(-1));

        (await _sut.HandleAsync(Command(code: WrongCode), Ct)).Error.ShouldBe(PhoneErrors.InvalidCode);
    }

    [Fact]
    public async Task Denied_role_after_the_right_code_is_invalid_code_and_the_code_is_spent()
    {
        var admin = Role.CreateSystem("Admin", "Administrators", Now.AddDays(-30));
        _user.AssignRole(admin.Id, assignedBy: null, Now.AddDays(-1));
        _roles.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns([admin]);

        (await _sut.HandleAsync(Command(), Ct)).Error.ShouldBe(PhoneErrors.InvalidCode);

        _code.ConsumedAt.ShouldBe(Now);
        _addedSessions.ShouldBeEmpty();
        AssertFailedAudit("role_denied", _user.Id);
    }

    [Fact]
    public async Task A_code_for_an_old_number_is_invalid_code()
    {
        var old = PhoneVerificationCode.Issue(_user.Id, PhoneCodePurpose.SignIn, "+60198765432", Code, "protected", TimeSpan.FromMinutes(5), Now.AddMinutes(-1));
        _codes.GetPendingAsync(_user.Id, PhoneCodePurpose.SignIn, Now, Arg.Any<CancellationToken>()).Returns(old);

        (await _sut.HandleAsync(Command(), Ct)).Error.ShouldBe(PhoneErrors.InvalidCode);

        await _codes.DidNotReceiveWithAnyArgs().TryConsumeAsync(default, default, Ct);
        _addedSessions.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_lost_consume_is_invalid_code()
    {
        _codes.TryConsumeAsync(_code.Id, Now, Arg.Any<CancellationToken>()).Returns(false);

        (await _sut.HandleAsync(Command(), Ct)).Error.ShouldBe(PhoneErrors.InvalidCode);

        _addedSessions.ShouldBeEmpty();
        AssertFailedAudit("lost_race", _user.Id);
    }

    [Fact]
    public async Task A_lost_session_save_is_invalid_code()
    {
        _unitOfWork.TrySaveChangesAsync(Arg.Any<CancellationToken>()).Returns(false);

        (await _sut.HandleAsync(Command(), Ct)).Error.ShouldBe(PhoneErrors.InvalidCode);

        _metrics.Received(1).RecordPhoneSignIn(PhoneSignInOutcome.Refused);
    }

    [Fact]
    public async Task A_verification_code_does_not_sign_in()
    {
        var verify = PhoneVerificationCode.Issue(_user.Id, PhoneCodePurpose.Verify, Number, Code, "protected", TimeSpan.FromMinutes(10), Now.AddMinutes(-1));
        _codes.GetPendingAsync(_user.Id, PhoneCodePurpose.SignIn, Now, Arg.Any<CancellationToken>()).Returns((PhoneVerificationCode?)null);
        _codes.GetPendingAsync(_user.Id, PhoneCodePurpose.Verify, Now, Arg.Any<CancellationToken>()).Returns(verify);

        (await _sut.HandleAsync(Command(), Ct)).Error.ShouldBe(PhoneErrors.InvalidCode);

        _addedSessions.ShouldBeEmpty();
    }

    [Fact]
    public async Task Formatting_is_normalized_at_verify()
    {
        (await _sut.HandleAsync(Command(number: "+60 12-345 6789"), Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Unconfirmed_email_still_signs_in()
    {
        var unconfirmed = User.Register("carol@example.com", "Carol", "en", "hash", Now.AddDays(-30)).Value;
        unconfirmed.EmailConfirmed.ShouldBeFalse();
        unconfirmed.VerifyPhoneNumber(Number, Now.AddDays(-2));
        var code = PhoneVerificationCode.Issue(unconfirmed.Id, PhoneCodePurpose.SignIn, Number, Code, "protected", TimeSpan.FromMinutes(5), Now.AddMinutes(-1));
        _users.GetByPhoneNumberAsync(Number, Arg.Any<CancellationToken>()).Returns(unconfirmed);
        _codes.GetPendingAsync(unconfirmed.Id, PhoneCodePurpose.SignIn, Now, Arg.Any<CancellationToken>()).Returns(code);
        _codes.TryClaimAttemptAsync(code.Id, 5, Now, Arg.Any<CancellationToken>()).Returns(true);
        _codes.TryConsumeAsync(code.Id, Now, Arg.Any<CancellationToken>()).Returns(true);

        (await _sut.HandleAsync(Command(), Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void The_command_never_prints_the_number_or_the_code()
    {
        var text = Command().ToString();

        text.ShouldNotContain("60123456789");
        text.ShouldNotContain(Code);
    }

    private static VerifyPhoneSignInCommand Command(string number = Number, string code = Code) => new(number, code, "Pixel 9");

    private void AssertFailedAudit(string reason, Guid? userId)
    {
        var failed = _auditEntries.Where(entry => entry.EventType == AuthAuditEvents.PhoneSignInFailed).ShouldHaveSingleItem();
        failed.Succeeded.ShouldBeFalse();
        failed.FailureReason.ShouldBe(reason);
        failed.UserId.ShouldBe(userId);
    }
}
