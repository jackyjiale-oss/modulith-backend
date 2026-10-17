using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Application.Common.Identity;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Mfa;
using TemplateName.Modules.Auth.Application.Mfa.ConfirmTotp;
using TemplateName.Modules.Auth.Application.StepUp;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Users.Events;
using TemplateName.Modules.Auth.Infrastructure.Security;
using TemplateName.SharedKernel;
using TemplateName.UnitTests.Application;

namespace TemplateName.UnitTests.Auth.Mfa;

public sealed class ConfirmTotpCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 9, 0, 10, TimeSpan.Zero);
    private static readonly DateTimeOffset SignedInAt = Now.AddMinutes(-3);

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly ISessionRepository _sessions = Substitute.For<ISessionRepository>();
    private readonly IMfaMethodRepository _methods = Substitute.For<IMfaMethodRepository>();
    private readonly IRecoveryCodeRepository _recoveryCodes = Substitute.For<IRecoveryCodeRepository>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IAuthAuditWriter _auditWriter = Substitute.For<IAuthAuditWriter>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly RecordingLogger<ConfirmTotpCommandHandler> _logger = new();
    private readonly DataProtectionMfaSecretProtector _protector = new(new EphemeralDataProtectionProvider());
    private readonly List<AuthAuditLog> _auditEntries = [];
    private readonly List<RecoveryCode> _addedCodes = [];
    private readonly List<RecoveryCode> _removedCodes = [];
    private readonly byte[] _secret = Totp.NewSecret();
    private readonly User _user;
    private readonly UserSession _current;
    private readonly UserMfaMethod _pending;
    private readonly ConfirmTotpCommandHandler _sut;

    public ConfirmTotpCommandHandlerTests()
    {
        _user = User.Register("alice@example.com", "Alice", "en", "stored-hash", SignedInAt.AddDays(-1)).Value;
        _user.ConfirmEmail(SignedInAt.AddDays(-1));
        _user.ClearDomainEvents();
        _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_user);

        _current = StartSession();
        _sessions.GetByIdAsync(_current.Id, Arg.Any<CancellationToken>()).Returns(_current);
        _sessions.GetActiveByUserAsync(_user.Id, Now, Arg.Any<CancellationToken>()).Returns([_current]);

        _pending = UserMfaMethod.StartTotp(_user.Id, "Phone", _protector.Protect(_secret), Now.AddMinutes(-1));
        _methods.GetTotpAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_pending);
        _recoveryCodes.ListByUserAsync(_user.Id, Arg.Any<CancellationToken>()).Returns([]);
        _recoveryCodes.Add(Arg.Do<RecoveryCode>(_addedCodes.Add));
        _recoveryCodes.Remove(Arg.Do<RecoveryCode>(_removedCodes.Add));

        _currentUser.UserId.Returns(_user.Id);
        _currentUser.SessionId.Returns(_current.Id);
        _currentUser.AuthTime.Returns(SignedInAt);
        _currentUser.AuthMethods.Returns(["pwd"]);
        _auditWriter.Record(Arg.Do<AuthAuditLog>(_auditEntries.Add));

        var time = new FakeTimeProvider(Now);
        _sut = new ConfirmTotpCommandHandler(
            _users,
            _sessions,
            _methods,
            _protector,
            new RecoveryCodeIssuer(_recoveryCodes),
            new MfaChangeEffects(_sessions),
            new StepUpPolicy(_currentUser, Options.Create(new StepUpOptions()), time),
            _currentUser,
            _auditWriter,
            _unitOfWork,
            _logger,
            time);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string CurrentCode => Totp.ComputeCode(_secret, Totp.TimeStepAt(Now));

    [Fact]
    public async Task Confirm_enables_rotates_the_stamp_revokes_other_sessions_and_keeps_the_current_one()
    {
        var oldStamp = _user.SecurityStamp;
        var other = StartSession();
        var third = StartSession();
        _sessions.GetActiveByUserAsync(_user.Id, Now, Arg.Any<CancellationToken>()).Returns([other, _current, third]);

        var result = await _sut.HandleAsync(new ConfirmTotpCommand(CurrentCode), Ct);

        result.IsSuccess.ShouldBeTrue();

        // The code's step is recorded at once, so the same code cannot step up or sign in afterwards (D3).
        _pending.IsConfirmed.ShouldBeTrue();
        _pending.ConfirmedAt.ShouldBe(Now);
        _pending.LastUsedTimeStep.ShouldBe(Totp.TimeStepAt(Now));
        await _methods.DidNotReceiveWithAnyArgs().TryClaimTimeStepAsync(default, default, default, Ct);

        _user.SecurityStamp.ShouldNotBe(oldStamp);
        _user.DomainEvents.ShouldHaveSingleItem().ShouldBe(new MfaChangedDomainEvent(_user.Id, MfaChange.Enabled));
        other.RevokedReason.ShouldBe(SessionRevokedReason.MfaChanged);
        third.RevokedReason.ShouldBe(SessionRevokedReason.MfaChanged);
        _current.RevokedAt.ShouldBeNull();
        _current.SecurityStamp.ShouldBe(_user.SecurityStamp);

        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.MfaEnabled);
        audit.Succeeded.ShouldBeTrue();
        audit.UserId.ShouldBe(_user.Id);
        audit.SessionId.ShouldBe(_current.Id);
        audit.Details.ShouldBe("""{"revokedSessionCount":2}""");

        // One save: the method, the codes, the stamp, the sessions, the audit row and the outbox row commit together.
        await _unitOfWork.Received(1).SaveChangesAsync(Ct);
        await _users.DidNotReceiveWithAnyArgs().RecordFailedSignInAsync(default, default, default, default, Ct);
    }

    [Fact]
    public async Task Confirm_returns_ten_display_codes_and_stores_only_hashes()
    {
        var oldCodes = new[]
        {
            RecoveryCode.Create(_user.Id, RecoveryCodeFormat.Hash(RecoveryCodeFormat.Generate()), Now.AddDays(-9)),
            RecoveryCode.Create(_user.Id, RecoveryCodeFormat.Hash(RecoveryCodeFormat.Generate()), Now.AddDays(-9)),
        };
        _recoveryCodes.ListByUserAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(oldCodes);

        var result = await _sut.HandleAsync(new ConfirmTotpCommand(CurrentCode), Ct);

        var codes = result.Value.RecoveryCodes;
        codes.Count.ShouldBe(RecoveryCodeFormat.Count);
        codes.Distinct(StringComparer.Ordinal).Count().ShouldBe(RecoveryCodeFormat.Count);
        codes.ShouldAllBe(code => System.Text.RegularExpressions.Regex.IsMatch(code, "^[2-9A-HJ-NP-Z]{4}(-[2-9A-HJ-NP-Z]{4}){3}$"));

        // The set is replaced: the old codes are removed and only the hash of each new one is stored.
        _removedCodes.ShouldBe(oldCodes);
        _addedCodes.Count.ShouldBe(RecoveryCodeFormat.Count);
        _addedCodes.ShouldAllBe(code => code.UserId == _user.Id && code.CreatedAt == Now && code.UsedAt == null);
        _addedCodes.Select(code => Convert.ToHexString(code.CodeHash)).ShouldBe(
            codes.Select(code => Convert.ToHexString(RecoveryCodeFormat.Hash(RecoveryCodeFormat.Normalize(code)!))),
            ignoreOrder: true);

        result.Value.ToString().ShouldBe(nameof(RecoveryCodesResponse));
        new ConfirmTotpCommand(CurrentCode).ToString().ShouldBe(nameof(ConfirmTotpCommand));
    }

    [Fact]
    public async Task Confirm_with_wrong_code_is_invalid_and_not_counted()
    {
        // The caller made this secret a moment ago, so a wrong code guesses nothing: it is not counted toward the lockout.
        var wrong = CurrentCode == "000000" ? "111111" : "000000";

        var result = await _sut.HandleAsync(new ConfirmTotpCommand(wrong), Ct);

        result.Error.ShouldBe(MfaErrors.InvalidCode);
        _pending.IsConfirmed.ShouldBeFalse();
        await _users.DidNotReceiveWithAnyArgs().RecordFailedSignInAsync(default, default, default, default, Ct);
        _user.AccessFailedCount.ShouldBe(0);
        _user.DomainEvents.ShouldBeEmpty();
        _recoveryCodes.DidNotReceiveWithAnyArgs().Add(default!);
        await _sessions.DidNotReceiveWithAnyArgs().GetActiveByUserAsync(default, default, Ct);
        _auditEntries.ShouldBeEmpty();
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Code_one_step_old_still_confirms()
    {
        var previous = Totp.ComputeCode(_secret, Totp.TimeStepAt(Now) - 1);

        var result = await _sut.HandleAsync(new ConfirmTotpCommand(previous), Ct);

        result.IsSuccess.ShouldBeTrue();
        _pending.LastUsedTimeStep.ShouldBe(Totp.TimeStepAt(Now) - 1);
    }

    [Fact]
    public async Task Confirm_requires_step_up()
    {
        _currentUser.AuthTime.Returns(Now - TimeSpan.FromMinutes(10));

        var result = await _sut.HandleAsync(new ConfirmTotpCommand(CurrentCode), Ct);

        result.Error.ShouldBe(MfaErrors.StepUpRequired);
        await _methods.DidNotReceiveWithAnyArgs().GetTotpAsync(default, Ct);
        _pending.IsConfirmed.ShouldBeFalse();
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Confirm_without_a_pending_method_is_not_pending()
    {
        _methods.GetTotpAsync(_user.Id, Arg.Any<CancellationToken>()).Returns((UserMfaMethod?)null);
        (await _sut.HandleAsync(new ConfirmTotpCommand(CurrentCode), Ct)).Error.ShouldBe(MfaErrors.SetupNotPending);

        // A confirmed method is no setup either: confirming again would issue a second set of codes.
        _pending.Confirm(Totp.TimeStepAt(Now) - 5, Now.AddMinutes(-3));
        _methods.GetTotpAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_pending);
        (await _sut.HandleAsync(new ConfirmTotpCommand(CurrentCode), Ct)).Error.ShouldBe(MfaErrors.SetupNotPending);

        _user.DomainEvents.ShouldBeEmpty();
        _recoveryCodes.DidNotReceiveWithAnyArgs().Add(default!);
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Unreadable_pending_secret_is_not_pending_and_logs_the_method_id_only()
    {
        // As after a key-ring loss between setup and confirm: the user must start the setup again.
        var foreign = new DataProtectionMfaSecretProtector(new EphemeralDataProtectionProvider()).Protect(_secret);
        var unreadable = UserMfaMethod.StartTotp(_user.Id, "Phone", foreign, Now.AddMinutes(-1));
        _methods.GetTotpAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(unreadable);

        var result = await _sut.HandleAsync(new ConfirmTotpCommand(CurrentCode), Ct);

        result.Error.ShouldBe(MfaErrors.SetupNotPending);
        unreadable.IsConfirmed.ShouldBeFalse();
        var entry = _logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Error);
        entry.Message.ShouldBe($"Could not read the TOTP secret of MFA method {unreadable.Id}");
        entry.Properties.Keys.Where(key => key != "{OriginalFormat}").ShouldBe(["MethodId"]);
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("no-session")]
    [InlineData("unknown-user")]
    [InlineData("suspended")]
    [InlineData("unknown-session")]
    [InlineData("revoked-session")]
    [InlineData("changed-stamp")]
    public async Task Caller_without_an_active_session_of_their_own_is_not_found(string scenario)
    {
        switch (scenario)
        {
            case "anonymous":
                _currentUser.UserId.Returns((Guid?)null);
                break;
            case "no-session":
                _currentUser.SessionId.Returns((Guid?)null);
                break;
            case "unknown-user":
                _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns((User?)null);
                break;
            case "suspended":
                _user.Suspend(Now);
                break;
            case "unknown-session":
                _sessions.GetByIdAsync(_current.Id, Arg.Any<CancellationToken>()).Returns((UserSession?)null);
                break;
            case "revoked-session":
                _current.Revoke(SessionRevokedReason.Logout, Now);
                break;
            default:
                _user.ChangeMfa(MfaChange.Disabled, Now);
                break;
        }

        _user.ClearDomainEvents();

        var result = await _sut.HandleAsync(new ConfirmTotpCommand(CurrentCode), Ct);

        result.Error.Type.ShouldBe(ErrorType.NotFound);
        _pending.IsConfirmed.ShouldBeFalse();
        _user.DomainEvents.ShouldBeEmpty();
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Nothing_logged_contains_the_code_or_the_secret()
    {
        await _sut.HandleAsync(new ConfirmTotpCommand(CurrentCode), Ct);

        _logger.Entries.ShouldBeEmpty();
        _auditEntries.ShouldAllBe(entry => entry.Details == null
            || (!entry.Details.Contains(CurrentCode, StringComparison.Ordinal)
                && !entry.Details.Contains(Base32.Encode(_secret), StringComparison.Ordinal)));
    }

    private UserSession StartSession()
    {
        var (session, _) = UserSession.Start(
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
        return session;
    }
}
