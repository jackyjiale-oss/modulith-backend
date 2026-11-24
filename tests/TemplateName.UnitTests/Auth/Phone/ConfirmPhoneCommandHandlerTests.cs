using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Application.Common.Identity;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Phone;
using TemplateName.Modules.Auth.Application.Phone.Confirm;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Verification;

namespace TemplateName.UnitTests.Auth.Phone;

public sealed class ConfirmPhoneCommandHandlerTests
{
    private const string Number = "+60123456789";
    private const string Code = "042137";

    private static readonly DateTimeOffset Now = new(2026, 11, 24, 8, 0, 0, TimeSpan.Zero);

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly ISessionRepository _sessions = Substitute.For<ISessionRepository>();
    private readonly IPhoneVerificationCodeRepository _codes = Substitute.For<IPhoneVerificationCodeRepository>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IAuthAuditWriter _auditWriter = Substitute.For<IAuthAuditWriter>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly List<AuthAuditLog> _auditEntries = [];
    private readonly User _user = User.Register("alice@example.com", "Alice", "en", "hash", Now.AddDays(-1)).Value;
    private readonly PhoneVerificationCode _pending;
    private readonly ConfirmPhoneCommandHandler _sut;

    public ConfirmPhoneCommandHandlerTests()
    {
        var session = StartSession(_user);
        _pending = PhoneVerificationCode.Issue(_user.Id, PhoneCodePurpose.Verify, Number, Code, "protected", TimeSpan.FromMinutes(10), Now.AddMinutes(-1));
        _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_user);
        _sessions.GetByIdAsync(session.Id, Arg.Any<CancellationToken>()).Returns(session);
        _currentUser.UserId.Returns(_user.Id);
        _currentUser.SessionId.Returns(session.Id);
        _codes.GetPendingAsync(_user.Id, PhoneCodePurpose.Verify, Now, Arg.Any<CancellationToken>()).Returns(_pending);
        _codes.TryConsumeAsync(_pending.Id, Now, Arg.Any<CancellationToken>()).Returns(true);
        _codes.TryCountWrongAttemptAsync(_pending.Id, 5, Now, Arg.Any<CancellationToken>()).Returns(true);
        _unitOfWork.SaveChangesUnlessDuplicateAsync(UniqueIndexNames.UserPhoneNumber, Arg.Any<CancellationToken>()).Returns(true);
        _auditWriter.Record(Arg.Do<AuthAuditLog>(_auditEntries.Add));
        _sut = new ConfirmPhoneCommandHandler(
            _users,
            _sessions,
            _codes,
            _currentUser,
            _auditWriter,
            _unitOfWork,
            Options.Create(new PhoneOptions()),
            new FakeTimeProvider(Now));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_right_code_verifies_the_number()
    {
        var result = await _sut.HandleAsync(new ConfirmPhoneCommand(Code), Ct);

        result.IsSuccess.ShouldBeTrue();
        _user.PhoneNumber.ShouldBe(Number);
        _user.PhoneNumberVerifiedAt.ShouldBe(Now);
        _pending.ConsumedAt.ShouldBe(Now);
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.PhoneNumberVerified);
        audit.Succeeded.ShouldBeTrue();
        audit.UserId.ShouldBe(_user.Id);
        await _unitOfWork.Received(1).SaveChangesUnlessDuplicateAsync(UniqueIndexNames.UserPhoneNumber, Ct);
    }

    [Fact]
    public async Task A_wrong_code_counts_one_attempt()
    {
        var result = await _sut.HandleAsync(new ConfirmPhoneCommand("999999"), Ct);

        result.Error.ShouldBe(PhoneErrors.InvalidCode);
        await _codes.Received(1).TryCountWrongAttemptAsync(_pending.Id, 5, Now, Ct);
        await _codes.DidNotReceiveWithAnyArgs().TryConsumeAsync(default, default, Ct);
        _user.PhoneNumber.ShouldBeNull();
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesUnlessDuplicateAsync(string.Empty, Ct);
    }

    [Fact]
    public async Task The_fifth_wrong_attempt_ends_the_code()
    {
        // The repository's conditional update ends the code on the fifth try; the next lookup finds no pending code.
        _codes.TryCountWrongAttemptAsync(_pending.Id, 5, Now, Arg.Any<CancellationToken>()).Returns(true, true, true, true, true, false);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            (await _sut.HandleAsync(new ConfirmPhoneCommand("999999"), Ct)).Error.ShouldBe(PhoneErrors.InvalidCode);
        }

        _codes.GetPendingAsync(_user.Id, PhoneCodePurpose.Verify, Now, Arg.Any<CancellationToken>()).Returns((PhoneVerificationCode?)null);
        (await _sut.HandleAsync(new ConfirmPhoneCommand(Code), Ct)).Error.ShouldBe(PhoneErrors.InvalidCode);
        _user.PhoneNumber.ShouldBeNull();
    }

    [Fact]
    public async Task No_pending_code_is_invalid_code()
    {
        _codes.GetPendingAsync(_user.Id, PhoneCodePurpose.Verify, Now, Arg.Any<CancellationToken>()).Returns((PhoneVerificationCode?)null);

        (await _sut.HandleAsync(new ConfirmPhoneCommand(Code), Ct)).Error.ShouldBe(PhoneErrors.InvalidCode);
    }

    [Fact]
    public async Task A_code_another_request_consumed_first_is_invalid_code()
    {
        _codes.TryConsumeAsync(_pending.Id, Now, Arg.Any<CancellationToken>()).Returns(false);

        (await _sut.HandleAsync(new ConfirmPhoneCommand(Code), Ct)).Error.ShouldBe(PhoneErrors.InvalidCode);
        _user.PhoneNumber.ShouldBeNull();
    }

    [Fact]
    public async Task A_number_another_account_holds_is_taken_before_any_save()
    {
        // Checked before saving, so the unique index (whose refusal EF Core logs with the number) is reached only by a true race.
        _users.IsPhoneNumberTakenAsync(Number, _user.Id, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.HandleAsync(new ConfirmPhoneCommand(Code), Ct);

        result.Error.ShouldBe(PhoneErrors.PhoneNumberTaken);
        await _codes.Received(1).TryConsumeAsync(_pending.Id, Now, Ct);
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesUnlessDuplicateAsync(string.Empty, Ct);
        _auditEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_number_verified_by_someone_else_meanwhile_is_taken()
    {
        _user.VerifyPhoneNumber("+60198765432", Now.AddDays(-1));
        _unitOfWork.SaveChangesUnlessDuplicateAsync(UniqueIndexNames.UserPhoneNumber, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.HandleAsync(new ConfirmPhoneCommand(Code), Ct);

        result.Error.ShouldBe(PhoneErrors.PhoneNumberTaken);
    }

    [Fact]
    public void The_command_never_prints_the_code() =>
        new ConfirmPhoneCommand(Code).ToString().ShouldNotContain(Code);

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
