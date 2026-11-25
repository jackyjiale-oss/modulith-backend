using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Application.Common.Identity;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Phone;
using TemplateName.Modules.Auth.Application.Phone.Remove;
using TemplateName.Modules.Auth.Application.StepUp;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Verification;

namespace TemplateName.UnitTests.Auth.Phone;

public sealed class RemovePhoneCommandHandlerTests
{
    private const string Number = "+60123456789";

    private static readonly DateTimeOffset Now = new(2026, 11, 24, 8, 0, 0, TimeSpan.Zero);

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly ISessionRepository _sessions = Substitute.For<ISessionRepository>();
    private readonly IPhoneVerificationCodeRepository _codes = Substitute.For<IPhoneVerificationCodeRepository>();
    private readonly IMfaMethodRepository _mfaMethods = Substitute.For<IMfaMethodRepository>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IAuthAuditWriter _auditWriter = Substitute.For<IAuthAuditWriter>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly List<AuthAuditLog> _auditEntries = [];
    private readonly User _user = User.Register("alice@example.com", "Alice", "en", "hash", Now.AddDays(-1)).Value;
    private readonly UserSession _session;
    private readonly RemovePhoneCommandHandler _sut;

    public RemovePhoneCommandHandlerTests()
    {
        var session = _session = StartSession(_user);
        _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_user);
        _sessions.GetByIdAsync(session.Id, Arg.Any<CancellationToken>()).Returns(session);
        _currentUser.UserId.Returns(_user.Id);
        _currentUser.SessionId.Returns(session.Id);
        _currentUser.AuthTime.Returns(Now.AddMinutes(-1));
        _currentUser.AuthMethods.Returns(["pwd"]);
        _codes.ListPendingAsync(_user.Id, null, Now, Arg.Any<CancellationToken>()).Returns([]);
        _auditWriter.Record(Arg.Do<AuthAuditLog>(_auditEntries.Add));
        var time = new FakeTimeProvider(Now);
        _sut = new RemovePhoneCommandHandler(
            _users,
            _sessions,
            new PhoneChangeEffects(_sessions, _codes),
            _mfaMethods,
            new StepUpPolicy(_currentUser, Options.Create(new StepUpOptions()), time),
            _currentUser,
            _auditWriter,
            _unitOfWork,
            time);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Removes_with_step_up()
    {
        _user.VerifyPhoneNumber(Number, Now.AddDays(-1));
        var pending = PhoneVerificationCode.Issue(_user.Id, PhoneCodePurpose.Verify, "+60198765432", "111111", "protected", TimeSpan.FromMinutes(10), Now.AddMinutes(-1));
        _codes.ListPendingAsync(_user.Id, null, Now, Arg.Any<CancellationToken>()).Returns([pending]);

        var result = await _sut.HandleAsync(new RemovePhoneCommand(), Ct);

        result.IsSuccess.ShouldBeTrue();
        _user.PhoneNumber.ShouldBeNull();
        _user.PhoneNumberVerifiedAt.ShouldBeNull();
        pending.InvalidatedAt.ShouldBe(Now);
        _auditEntries.ShouldHaveSingleItem().EventType.ShouldBe(AuthAuditEvents.PhoneNumberRemoved);
        await _unitOfWork.Received(1).SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Without_step_up_is_refused_when_a_number_exists()
    {
        _user.VerifyPhoneNumber(Number, Now.AddDays(-1));
        _currentUser.AuthTime.Returns(Now.AddHours(-1));

        var result = await _sut.HandleAsync(new RemovePhoneCommand(), Ct);

        result.Error.ShouldBe(MfaErrors.StepUpRequired);
        _user.PhoneNumber.ShouldBe(Number);
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task An_account_with_mfa_needs_a_code_step_up()
    {
        _user.VerifyPhoneNumber(Number, Now.AddDays(-1));
        _mfaMethods.HasConfirmedAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(true);

        (await _sut.HandleAsync(new RemovePhoneCommand(), Ct)).Error.ShouldBe(MfaErrors.StepUpRequired);
    }

    [Fact]
    public async Task Without_a_number_succeeds_without_step_up_and_ends_pending_codes()
    {
        _currentUser.AuthTime.Returns((DateTimeOffset?)null);
        var pending = PhoneVerificationCode.Issue(_user.Id, PhoneCodePurpose.Verify, Number, "111111", "protected", TimeSpan.FromMinutes(10), Now.AddMinutes(-1));
        _codes.ListPendingAsync(_user.Id, null, Now, Arg.Any<CancellationToken>()).Returns([pending]);

        var result = await _sut.HandleAsync(new RemovePhoneCommand(), Ct);

        result.IsSuccess.ShouldBeTrue();
        pending.InvalidatedAt.ShouldBe(Now);
        _auditEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Removal_ends_the_other_sessions_and_pending_codes()
    {
        _user.VerifyPhoneNumber(Number, Now.AddDays(-1));
        var other = StartSession(_user);
        _sessions.GetActiveByUserAsync(_user.Id, Now, Arg.Any<CancellationToken>()).Returns([_session, other]);
        var signIn = PhoneVerificationCode.Issue(_user.Id, PhoneCodePurpose.SignIn, Number, "222222", "protected", TimeSpan.FromMinutes(5), Now.AddMinutes(-1));
        _codes.ListPendingAsync(_user.Id, null, Now, Arg.Any<CancellationToken>()).Returns([signIn]);

        (await _sut.HandleAsync(new RemovePhoneCommand(), Ct)).IsSuccess.ShouldBeTrue();

        other.RevokedReason.ShouldBe(SessionRevokedReason.PhoneNumberChanged);
        _session.RevokedAt.ShouldBeNull();
        _session.SecurityStamp.ShouldBe(_user.SecurityStamp);
        signIn.InvalidatedAt.ShouldBe(Now);
        _auditEntries.ShouldHaveSingleItem().Details.ShouldBe("""{"revokedSessionCount":1}""");
    }

    [Fact]
    public async Task Removal_without_a_number_ends_no_session()
    {
        var other = StartSession(_user);
        _sessions.GetActiveByUserAsync(_user.Id, Now, Arg.Any<CancellationToken>()).Returns([_session, other]);

        (await _sut.HandleAsync(new RemovePhoneCommand(), Ct)).IsSuccess.ShouldBeTrue();

        other.RevokedAt.ShouldBeNull();
    }

    private static UserSession StartSession(User user)
    {
        var (session, _) = UserSession.Start(
            user.Id,
            AuthenticationMethods.Password,
            "Phone",
            userAgent: null,
            ipAddress: null,
            user.SecurityStamp,
            [.. Enumerable.Range(1, 32).Select(value => (byte)value)],
            TimeSpan.FromDays(14),
            TimeSpan.FromDays(90),
            Now.AddHours(-1));
        return session;
    }
}
