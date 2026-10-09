using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Passwords;
using TemplateName.Modules.Auth.Application.Passwords.Reset;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Users.Events;
using TemplateName.Modules.Auth.Domain.Verification;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Auth;

public sealed class ResetPasswordCommandHandlerTests
{
    private const string Token = "presented-reset-token";
    private const string NewPassword = "a brand new passphrase";
    private const string NewHash = "new-hash";
    private const string OldHash = "old-hash";

    private static readonly DateTimeOffset Now = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);
    private static readonly byte[] TokenHash = [.. Enumerable.Range(1, 32).Select(value => (byte)value)];

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IVerificationCodeRepository _verificationCodes = Substitute.For<IVerificationCodeRepository>();
    private readonly ISessionRepository _sessions = Substitute.For<ISessionRepository>();
    private readonly IPasswordHasher _passwordHasher = Substitute.For<IPasswordHasher>();
    private readonly IBreachedPasswordChecker _breachedPasswordChecker = Substitute.For<IBreachedPasswordChecker>();
    private readonly ISecureTokenService _tokenService = Substitute.For<ISecureTokenService>();
    private readonly IAuthAuditWriter _auditWriter = Substitute.For<IAuthAuditWriter>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly List<AuthAuditLog> _auditEntries = [];
    private readonly User _user = User.Register("alice@example.com", "Alice", "en", OldHash, Now.AddDays(-1)).Value;
    private readonly ResetPasswordCommandHandler _sut;

    public ResetPasswordCommandHandlerTests()
    {
        _tokenService.Hash(Token).Returns(TokenHash);
        _passwordHasher.Hash(NewPassword).Returns(NewHash);
        _passwordHasher.Verify(Arg.Any<string>(), Arg.Any<string>()).Returns(PasswordVerification.Failed);
        _auditWriter.Record(Arg.Do<AuthAuditLog>(_auditEntries.Add));
        _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_user);
        _verificationCodes.TryConsumeAsync(Arg.Any<Guid>(), Arg.Any<VerificationPurpose>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _sessions.GetActiveByUserAsync(_user.Id, Now, Arg.Any<CancellationToken>()).Returns([]);
        _verificationCodes.GetPendingAsync(_user.Id, VerificationPurpose.PasswordReset, Now, Arg.Any<CancellationToken>()).Returns([]);
        _sut = new ResetPasswordCommandHandler(
            _users,
            _verificationCodes,
            _sessions,
            _passwordHasher,
            _breachedPasswordChecker,
            _tokenService,
            _auditWriter,
            _unitOfWork,
            Options.Create(new PasswordOptions()),
            new FakeTimeProvider(Now));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Valid_token_changes_the_password_consumes_the_code_and_audits()
    {
        var code = IssueCode(VerificationPurpose.PasswordReset);
        var oldStamp = _user.SecurityStamp;

        var result = await _sut.HandleAsync(Command(), Ct);

        result.IsSuccess.ShouldBeTrue();
        _user.PasswordHash.ShouldBe(NewHash);
        _user.SecurityStamp.ShouldNotBe(oldStamp);
        _user.PasswordHistory.Select(entry => entry.PasswordHash).ShouldBe([OldHash, NewHash]);
        _user.DomainEvents.OfType<PasswordChangedDomainEvent>().ShouldHaveSingleItem();
        code.ConsumedAt.ShouldBe(Now);
        await _verificationCodes.Received(1).TryConsumeAsync(code.Id, VerificationPurpose.PasswordReset, Now, CancellationToken.None);
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.PasswordReset);
        audit.Succeeded.ShouldBeTrue();
        audit.UserId.ShouldBe(_user.Id);
        await _unitOfWork.Received(1).SaveChangesAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Reset_invalidates_every_other_pending_reset_link()
    {
        // A second link (two forgots, or a leaked one) must not outlive the reset.
        var code = IssueCode(VerificationPurpose.PasswordReset);
        var other = OtherPendingResetCode();
        _verificationCodes.GetPendingAsync(_user.Id, VerificationPurpose.PasswordReset, Now, Arg.Any<CancellationToken>()).Returns([other]);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.IsSuccess.ShouldBeTrue();
        other.InvalidatedAt.ShouldBe(Now);
        code.ConsumedAt.ShouldBe(Now);
        code.InvalidatedAt.ShouldBeNull();
        await _verificationCodes.Received(1).GetPendingAsync(_user.Id, VerificationPurpose.PasswordReset, Now, CancellationToken.None);
        await _unitOfWork.Received(1).SaveChangesAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Failed_reset_leaves_the_other_pending_reset_links_alone()
    {
        // A reused password spends the presented link, but the reset did not happen, so other links keep working.
        IssueCode(VerificationPurpose.PasswordReset);
        var other = OtherPendingResetCode();
        _verificationCodes.GetPendingAsync(_user.Id, VerificationPurpose.PasswordReset, Now, Arg.Any<CancellationToken>()).Returns([other]);
        _passwordHasher.Verify(OldHash, NewPassword).Returns(PasswordVerification.Success);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(UserErrors.PasswordReused);
        other.InvalidatedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Reset_confirms_email_and_clears_lockout()
    {
        IssueCode(VerificationPurpose.PasswordReset);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            _user.RecordFailedSignIn(Now.AddMinutes(-1), maxFailedAttempts: 5, TimeSpan.FromMinutes(15));
        }

        _user.IsLockedOut(Now).ShouldBeTrue();
        _user.EmailConfirmed.ShouldBeFalse();

        var result = await _sut.HandleAsync(Command(), Ct);

        result.IsSuccess.ShouldBeTrue();
        _user.EmailConfirmed.ShouldBeTrue();
        _user.IsLockedOut(Now).ShouldBeFalse();
        _user.LockoutEnd.ShouldBeNull();
        _user.AccessFailedCount.ShouldBe(0);
    }

    [Fact]
    public async Task Reset_revokes_every_active_session()
    {
        IssueCode(VerificationPurpose.PasswordReset);
        var sessions = new[] { StartSession(), StartSession() };
        _sessions.GetActiveByUserAsync(_user.Id, Now, Arg.Any<CancellationToken>()).Returns(sessions);

        await _sut.HandleAsync(Command(), Ct);

        sessions.ShouldAllBe(session => session.RevokedAt == Now && session.RevokedReason == SessionRevokedReason.PasswordChanged);
    }

    [Fact]
    public async Task User_without_a_password_sets_the_first_one()
    {
        var invited = User.Register("invited@example.com", "Invited", "en", passwordHash: null, Now.AddDays(-1)).Value;
        _users.GetByIdAsync(invited.Id, Arg.Any<CancellationToken>()).Returns(invited);
        IssueCode(VerificationPurpose.PasswordReset, invited.Id);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.IsSuccess.ShouldBeTrue();
        invited.PasswordHash.ShouldBe(NewHash);
        invited.EmailConfirmed.ShouldBeTrue();
    }

    [Fact]
    public async Task Unknown_token_returns_invalid_token_and_changes_nothing()
    {
        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(VerificationErrors.InvalidToken);
        await AssertNothingChangedAsync(expectedUserId: null);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Token_of_another_purpose_or_expired_returns_invalid_token(bool isOtherPurpose)
    {
        // An email-confirmation code issued a minute ago, or a reset code issued 31 minutes ago (its lifetime is 30).
        var code = isOtherPurpose
            ? IssueCode(VerificationPurpose.EmailVerify)
            : IssueCode(VerificationPurpose.PasswordReset, issuedAt: Now.AddMinutes(-31));

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(VerificationErrors.InvalidToken);
        code.ConsumedAt.ShouldBeNull();
        await AssertNothingChangedAsync(expectedUserId: _user.Id);
    }

    [Fact]
    public async Task Claim_lost_to_a_simultaneous_reset_returns_invalid_token()
    {
        var code = IssueCode(VerificationPurpose.PasswordReset);
        _verificationCodes.TryConsumeAsync(code.Id, VerificationPurpose.PasswordReset, Now, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(VerificationErrors.InvalidToken);
        code.ConsumedAt.ShouldBeNull();
        await AssertNothingChangedAsync(expectedUserId: _user.Id);
    }

    [Fact]
    public async Task Suspended_user_returns_invalid_token_and_is_not_reactivated()
    {
        var code = IssueCode(VerificationPurpose.PasswordReset);
        _user.Suspend(Now.AddMinutes(-1));

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(VerificationErrors.InvalidToken);
        _user.Status.ShouldBe(UserStatus.Suspended);
        code.ConsumedAt.ShouldBeNull();
        await AssertNothingChangedAsync(expectedUserId: _user.Id);
    }

    [Fact]
    public async Task Code_whose_user_no_longer_exists_returns_invalid_token()
    {
        IssueCode(VerificationPurpose.PasswordReset, userId: Guid.NewGuid());

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(VerificationErrors.InvalidToken);
        await _verificationCodes.DidNotReceiveWithAnyArgs().TryConsumeAsync(default, default, default, Ct);
    }

    [Fact]
    public async Task Breached_password_returns_auth_password_breached_and_keeps_the_token_usable()
    {
        var code = IssueCode(VerificationPurpose.PasswordReset);
        _breachedPasswordChecker.IsBreachedAsync(NewPassword, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(UserErrors.PasswordBreached);
        code.ConsumedAt.ShouldBeNull();
        await _verificationCodes.DidNotReceiveWithAnyArgs().TryConsumeAsync(default, default, default, Ct);
        _user.PasswordHash.ShouldBe(OldHash);
        _auditEntries.ShouldHaveSingleItem().FailureReason.ShouldBe(UserErrors.PasswordBreached.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Current_password_again_returns_auth_password_reused_and_uses_up_the_link(bool needsRehash)
    {
        var code = IssueCode(VerificationPurpose.PasswordReset);
        _passwordHasher.Verify(OldHash, NewPassword).Returns(needsRehash ? PasswordVerification.SuccessRehashNeeded : PasswordVerification.Success);

        var result = await _sut.HandleAsync(Command(), Ct);

        // The reuse check runs only after the link is used up, so it can never be a free oracle for the account's passwords.
        result.Error.ShouldBe(UserErrors.PasswordReused);
        await _verificationCodes.Received(1).TryConsumeAsync(code.Id, VerificationPurpose.PasswordReset, Now, CancellationToken.None);
        code.ConsumedAt.ShouldBe(Now);
        _user.PasswordHash.ShouldBe(OldHash);
        _passwordHasher.DidNotReceiveWithAnyArgs().Hash(default!);
        _auditEntries.ShouldHaveSingleItem().FailureReason.ShouldBe(UserErrors.PasswordReused.Code);

        // A second attempt with the same link, even with an acceptable password, is refused like a used token.
        _passwordHasher.Verify(OldHash, NewPassword).Returns(PasswordVerification.Failed);
        var retry = await _sut.HandleAsync(Command(), Ct);

        retry.Error.ShouldBe(VerificationErrors.InvalidToken);
        _user.PasswordHash.ShouldBe(OldHash);
        await _verificationCodes.Received(1).TryConsumeAsync(code.Id, VerificationPurpose.PasswordReset, Now, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(129)]
    [InlineData(1024 * 1024)]
    public async Task Oversized_password_is_rejected_before_hashing(int length)
    {
        var pipeline = new ValidationDecorator.CommandHandler<ResetPasswordCommand>(
            _sut,
            [new ResetPasswordCommandValidator(Options.Create(new PasswordOptions()))]);

        var result = await pipeline.HandleAsync(Command() with { NewPassword = new string('p', length) }, Ct);

        result.Error.ShouldBeOfType<ValidationError>().Errors.Keys.ShouldContain("newPassword");
        await _verificationCodes.DidNotReceiveWithAnyArgs().GetByTokenHashAsync(default!, Ct);
        await _breachedPasswordChecker.DidNotReceiveWithAnyArgs().IsBreachedAsync(default!, Ct);
        _passwordHasher.DidNotReceiveWithAnyArgs().Hash(default!);
        _passwordHasher.DidNotReceiveWithAnyArgs().Verify(default!, default!);
    }

    [Fact]
    public void Command_text_never_shows_the_token_or_the_password()
    {
        var text = Command().ToString();

        text.ShouldNotContain(Token);
        text.ShouldNotContain(NewPassword);
    }

    private static ResetPasswordCommand Command() => new(Token, NewPassword);

    private async Task AssertNothingChangedAsync(Guid? expectedUserId)
    {
        _user.PasswordHash.ShouldBe(OldHash);
        _user.EmailConfirmed.ShouldBeFalse();
        _passwordHasher.DidNotReceiveWithAnyArgs().Hash(default!);
        await _sessions.DidNotReceiveWithAnyArgs().GetActiveByUserAsync(default, default, Ct);
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.PasswordResetFailed);
        audit.Succeeded.ShouldBeFalse();
        audit.UserId.ShouldBe(expectedUserId);
        audit.FailureReason.ShouldBe(VerificationErrors.InvalidToken.Code);
        await _unitOfWork.ReceivedWithAnyArgs(1).SaveChangesAsync(Ct);
    }

    private VerificationCode OtherPendingResetCode()
        => VerificationCode.Issue(
            _user.Id,
            VerificationPurpose.PasswordReset,
            _user.NormalizedEmail,
            [.. Enumerable.Repeat((byte)9, 32)],
            "protected",
            TimeSpan.FromMinutes(30),
            null,
            Now.AddMinutes(-2));

    private VerificationCode IssueCode(VerificationPurpose purpose, Guid? userId = null, DateTimeOffset? issuedAt = null)
    {
        var code = VerificationCode.Issue(
            userId ?? _user.Id,
            purpose,
            _user.NormalizedEmail,
            TokenHash,
            "protected",
            TimeSpan.FromMinutes(30),
            null,
            issuedAt ?? Now.AddMinutes(-1));
        _verificationCodes.GetByTokenHashAsync(TokenHash, Arg.Any<CancellationToken>()).Returns(code);
        return code;
    }

    private UserSession StartSession()
        => UserSession.Start(_user.Id, "pwd", "Device", null, null, _user.SecurityStamp, new byte[32], TimeSpan.FromDays(14), TimeSpan.FromDays(90), Now.AddHours(-1)).Session;
}
