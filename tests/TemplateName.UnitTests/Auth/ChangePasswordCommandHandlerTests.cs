using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Passwords;
using TemplateName.Modules.Auth.Application.Passwords.Change;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Verification;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Auth;

public sealed class ChangePasswordCommandHandlerTests
{
    private const string CurrentPassword = "the current passphrase";
    private const string NewPassword = "a brand new passphrase";
    private const string CurrentHash = "hash-5";
    private const string NewHash = "new-hash";

    private static readonly DateTimeOffset Now = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly ISessionRepository _sessions = Substitute.For<ISessionRepository>();
    private readonly IVerificationCodeRepository _verificationCodes = Substitute.For<IVerificationCodeRepository>();
    private readonly IPasswordHasher _passwordHasher = Substitute.For<IPasswordHasher>();
    private readonly IBreachedPasswordChecker _breachedPasswordChecker = Substitute.For<IBreachedPasswordChecker>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IAuthAuditWriter _auditWriter = Substitute.For<IAuthAuditWriter>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly List<AuthAuditLog> _auditEntries = [];
    private readonly User _user;
    private readonly ChangePasswordCommandHandler _sut;

    public ChangePasswordCommandHandlerTests()
    {
        // hash-0 at registration, then five changes: the history keeps hash-1 to hash-5, and hash-5 is the current one.
        _user = User.Register("alice@example.com", "Alice", "en", "hash-0", Now.AddDays(-10)).Value;
        for (var change = 1; change <= 5; change++)
        {
            _user.ChangePassword($"hash-{change}", historyCount: 5, Now.AddDays(-10 + change));
        }

        _user.ConfirmEmail(Now.AddDays(-10));
        _user.ClearDomainEvents();

        _currentUser.UserId.Returns(_user.Id);
        _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_user);
        _passwordHasher.Verify(Arg.Any<string>(), Arg.Any<string>()).Returns(PasswordVerification.Failed);
        _passwordHasher.Verify(CurrentHash, CurrentPassword).Returns(PasswordVerification.Success);
        _passwordHasher.Hash(NewPassword).Returns(NewHash);
        _auditWriter.Record(Arg.Do<AuthAuditLog>(_auditEntries.Add));
        _sessions.GetActiveByUserAsync(_user.Id, Now, Arg.Any<CancellationToken>()).Returns([]);
        _verificationCodes.GetPendingAsync(_user.Id, VerificationPurpose.PasswordReset, Now, Arg.Any<CancellationToken>()).Returns([]);
        _sut = new ChangePasswordCommandHandler(
            _users,
            _sessions,
            _verificationCodes,
            _passwordHasher,
            _breachedPasswordChecker,
            _currentUser,
            _auditWriter,
            _unitOfWork,
            Options.Create(new PasswordOptions()),
            new FakeTimeProvider(Now));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Change_keeps_the_current_session()
    {
        var current = StartSession();
        var other = StartSession();
        var third = StartSession();
        _currentUser.SessionId.Returns(current.Id);
        _sessions.GetActiveByUserAsync(_user.Id, Now, Arg.Any<CancellationToken>()).Returns([other, current, third]);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.IsSuccess.ShouldBeTrue();
        current.RevokedAt.ShouldBeNull();
        other.RevokedReason.ShouldBe(SessionRevokedReason.PasswordChanged);
        third.RevokedReason.ShouldBe(SessionRevokedReason.PasswordChanged);

        // The kept session takes the new stamp, so its next refresh is not refused as a stamp mismatch; the others keep the old one.
        current.SecurityStamp.ShouldBe(_user.SecurityStamp);
        other.SecurityStamp.ShouldNotBe(_user.SecurityStamp);
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.PasswordChanged);
        audit.Succeeded.ShouldBeTrue();
        audit.UserId.ShouldBe(_user.Id);
        audit.SessionId.ShouldBe(current.Id);
        audit.Details.ShouldBe("""{"revokedSessionCount":2}""");
        await _unitOfWork.Received(1).SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Change_invalidates_every_pending_reset_link()
    {
        // A reset link emailed earlier (or leaked) must not outlive the password it was meant to replace.
        var pending = IssueResetCode();
        var another = IssueResetCode();
        _verificationCodes.GetPendingAsync(_user.Id, VerificationPurpose.PasswordReset, Now, Arg.Any<CancellationToken>()).Returns([pending, another]);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.IsSuccess.ShouldBeTrue();
        pending.InvalidatedAt.ShouldBe(Now);
        another.InvalidatedAt.ShouldBe(Now);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Failed_change_leaves_pending_reset_links_alone()
    {
        var pending = IssueResetCode();
        _verificationCodes.GetPendingAsync(_user.Id, VerificationPurpose.PasswordReset, Now, Arg.Any<CancellationToken>()).Returns([pending]);

        var result = await _sut.HandleAsync(Command() with { CurrentPassword = "a wrong passphrase" }, Ct);

        result.Error.ShouldBe(UserErrors.CurrentPasswordIncorrect);
        pending.InvalidatedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Change_stores_the_new_password_and_rotates_the_stamp()
    {
        var oldStamp = _user.SecurityStamp;

        var result = await _sut.HandleAsync(Command(), Ct);

        result.IsSuccess.ShouldBeTrue();
        _user.PasswordHash.ShouldBe(NewHash);
        _user.SecurityStamp.ShouldNotBe(oldStamp);
        _user.PasswordHistory.Select(entry => entry.PasswordHash).ShouldBe(["hash-2", "hash-3", "hash-4", "hash-5", NewHash]);
    }

    [Theory]
    [InlineData("hash-1")]
    [InlineData("hash-2")]
    [InlineData("hash-3")]
    [InlineData("hash-4")]
    [InlineData("hash-5")]
    public async Task Reuse_check_covers_current_and_previous_four_hashes(string matchingHash)
    {
        _passwordHasher.Verify(matchingHash, NewPassword).Returns(PasswordVerification.Success);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(UserErrors.PasswordReused);
        _user.PasswordHash.ShouldBe(CurrentHash);
        _passwordHasher.DidNotReceiveWithAnyArgs().Hash(default!);
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.PasswordChangeFailed);
        audit.FailureReason.ShouldBe(UserErrors.PasswordReused.Code);
    }

    [Fact]
    public async Task Password_older_than_the_history_may_be_used_again()
    {
        // hash-0 dropped out of the five-entry history, so it is not checked any more.
        _passwordHasher.Verify("hash-0", NewPassword).Returns(PasswordVerification.Success);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.IsSuccess.ShouldBeTrue();
        _passwordHasher.DidNotReceive().Verify("hash-0", NewPassword);
    }

    [Fact]
    public async Task Wrong_current_password_returns_400_audits_and_changes_nothing()
    {
        var other = StartSession();
        _sessions.GetActiveByUserAsync(_user.Id, Now, Arg.Any<CancellationToken>()).Returns([other]);

        var result = await _sut.HandleAsync(Command() with { CurrentPassword = "not the password" }, Ct);

        result.Error.ShouldBe(UserErrors.CurrentPasswordIncorrect);
        _user.PasswordHash.ShouldBe(CurrentHash);
        _user.AccessFailedCount.ShouldBe(0);
        other.RevokedAt.ShouldBeNull();
        await _breachedPasswordChecker.DidNotReceiveWithAnyArgs().IsBreachedAsync(default!, Ct);
        await _users.DidNotReceiveWithAnyArgs().RecordFailedSignInAsync(default, default, default, default, Ct);
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.PasswordChangeFailed);
        audit.Succeeded.ShouldBeFalse();
        audit.UserId.ShouldBe(_user.Id);
        audit.FailureReason.ShouldBe(UserErrors.CurrentPasswordIncorrect.Code);
        await _unitOfWork.Received(1).SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task User_without_a_password_spends_the_verification_cost_and_fails()
    {
        var invited = User.Register("invited@example.com", "Invited", "en", passwordHash: null, Now.AddDays(-1)).Value;
        _currentUser.UserId.Returns(invited.Id);
        _users.GetByIdAsync(invited.Id, Arg.Any<CancellationToken>()).Returns(invited);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(UserErrors.CurrentPasswordIncorrect);
        _passwordHasher.Received(1).SpendVerificationCost(CurrentPassword);
        invited.PasswordHash.ShouldBeNull();
    }

    [Fact]
    public async Task Breached_password_returns_auth_password_breached_after_the_current_password_is_proven()
    {
        _breachedPasswordChecker.IsBreachedAsync(NewPassword, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(UserErrors.PasswordBreached);
        _user.PasswordHash.ShouldBe(CurrentHash);
        _auditEntries.ShouldHaveSingleItem().FailureReason.ShouldBe(UserErrors.PasswordBreached.Code);
    }

    [Fact]
    public async Task Missing_or_suspended_user_fails_as_not_found()
    {
        _user.Suspend(Now);

        var suspended = await _sut.HandleAsync(Command(), Ct);
        _currentUser.UserId.Returns(Guid.NewGuid());
        var missing = await _sut.HandleAsync(Command(), Ct);
        _currentUser.UserId.Returns((Guid?)null);
        var anonymous = await _sut.HandleAsync(Command(), Ct);

        suspended.Error.Type.ShouldBe(ErrorType.NotFound);
        missing.Error.Type.ShouldBe(ErrorType.NotFound);
        anonymous.Error.Type.ShouldBe(ErrorType.NotFound);
        _passwordHasher.DidNotReceiveWithAnyArgs().Hash(default!);
        _user.PasswordHash.ShouldBe(CurrentHash);
    }

    [Theory]
    [InlineData(129)]
    [InlineData(1024 * 1024)]
    public async Task Oversized_password_is_rejected_before_hashing(int length)
    {
        var pipeline = new ValidationDecorator.CommandHandler<ChangePasswordCommand>(
            _sut,
            [new ChangePasswordCommandValidator(Options.Create(new PasswordOptions()))]);

        var oversized = new string('p', length);
        var withCurrent = await pipeline.HandleAsync(Command() with { CurrentPassword = oversized }, Ct);
        var withNew = await pipeline.HandleAsync(Command() with { NewPassword = oversized }, Ct);

        withCurrent.Error.ShouldBeOfType<ValidationError>().Errors.Keys.ShouldContain("currentPassword");
        withNew.Error.ShouldBeOfType<ValidationError>().Errors.Keys.ShouldContain("newPassword");
        await _users.DidNotReceiveWithAnyArgs().GetByIdAsync(default, Ct);
        _passwordHasher.DidNotReceiveWithAnyArgs().Hash(default!);
        _passwordHasher.DidNotReceiveWithAnyArgs().Verify(default!, default!);
    }

    [Fact]
    public void Command_text_never_shows_the_passwords()
    {
        var text = Command().ToString();

        text.ShouldNotContain(CurrentPassword);
        text.ShouldNotContain(NewPassword);
    }

    private static ChangePasswordCommand Command() => new(CurrentPassword, NewPassword);

    private VerificationCode IssueResetCode()
        => VerificationCode.Issue(
            _user.Id,
            VerificationPurpose.PasswordReset,
            VerificationTrigger.SelfService,
            _user.NormalizedEmail,
            [.. Guid.NewGuid().ToByteArray(), .. Guid.NewGuid().ToByteArray()],
            "protected",
            TimeSpan.FromMinutes(30),
            null,
            Now.AddMinutes(-5));

    private UserSession StartSession()
        => UserSession.Start(_user.Id, "pwd", "Device", null, null, _user.SecurityStamp, new byte[32], TimeSpan.FromDays(14), TimeSpan.FromDays(90), Now.AddHours(-1)).Session;
}
