using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Application.Common.Identity;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Mfa;
using TemplateName.Modules.Auth.Application.Mfa.SetUpTotp;
using TemplateName.Modules.Auth.Application.StepUp;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Infrastructure.Security;

namespace TemplateName.UnitTests.Auth.Mfa;

public sealed class SetUpTotpCommandHandlerTests
{
    private const string Email = "alice@example.com";

    private static readonly DateTimeOffset Now = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SignedInAt = Now.AddMinutes(-2);

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly ISessionRepository _sessions = Substitute.For<ISessionRepository>();
    private readonly IMfaMethodRepository _methods = Substitute.For<IMfaMethodRepository>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IAuthAuditWriter _auditWriter = Substitute.For<IAuthAuditWriter>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly DataProtectionMfaSecretProtector _protector = new(new EphemeralDataProtectionProvider());
    private readonly MfaOptions _options = new() { Issuer = "Example Co" };
    private readonly List<AuthAuditLog> _auditEntries = [];
    private readonly List<UserMfaMethod> _added = [];
    private readonly User _user;
    private readonly UserSession _session;
    private readonly SetUpTotpCommandHandler _sut;

    public SetUpTotpCommandHandlerTests()
    {
        _user = User.Register(Email, "Alice", "en", "stored-hash", SignedInAt.AddDays(-1)).Value;
        _user.ConfirmEmail(SignedInAt.AddDays(-1));
        _user.ClearDomainEvents();
        _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_user);

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
        _currentUser.AuthTime.Returns(SignedInAt);
        _currentUser.AuthMethods.Returns(["pwd"]);
        _auditWriter.Record(Arg.Do<AuthAuditLog>(_auditEntries.Add));
        _methods.Add(Arg.Do<UserMfaMethod>(_added.Add));
        _unitOfWork.SaveChangesUnlessDuplicateAsync(UniqueIndexNames.MfaMethodPerUserAndType, Arg.Any<CancellationToken>()).Returns(true);

        var time = new FakeTimeProvider(Now);
        _sut = new SetUpTotpCommandHandler(
            _users,
            _sessions,
            _methods,
            _protector,
            new StepUpPolicy(_currentUser, Options.Create(new StepUpOptions()), time),
            _currentUser,
            _auditWriter,
            _unitOfWork,
            Options.Create(_options),
            time);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Setup_starts_a_pending_method_and_shows_the_secret_once()
    {
        var result = await _sut.HandleAsync(new SetUpTotpCommand(null), Ct);

        var method = _added.ShouldHaveSingleItem();
        method.UserId.ShouldBe(_user.Id);
        method.Type.ShouldBe(MfaMethodType.Totp);
        method.Label.ShouldBe(SetUpTotpCommandHandler.DefaultLabel);
        method.IsConfirmed.ShouldBeFalse();
        method.CreatedAt.ShouldBe(Now);

        // The answer carries the secret the stored value protects, as base32 and inside the URI of the configured issuer.
        var response = result.Value;
        response.MethodId.ShouldBe(method.Id);
        var secret = _protector.Unprotect(method.SecretEncrypted);
        secret.Length.ShouldBe(Totp.SecretLength);
        response.Secret.ShouldBe(Base32.Encode(secret));
        response.OtpAuthUri.ShouldBe(TotpUri.Build("Example Co", Email, secret));
        method.SecretEncrypted.ShouldNotContain(response.Secret);

        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.MfaSetupStarted);
        audit.Succeeded.ShouldBeTrue();
        audit.UserId.ShouldBe(_user.Id);
        audit.SessionId.ShouldBe(_session.Id);
        audit.Details.ShouldBe($$"""{"methodId":"{{method.Id}}"}""");
        await _unitOfWork.Received(1).SaveChangesUnlessDuplicateAsync(UniqueIndexNames.MfaMethodPerUserAndType, Ct);
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);

        // Enrollment changes no stamp and no session until the confirm.
        _user.DomainEvents.ShouldBeEmpty();
        _session.RevokedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Setup_trims_the_given_label()
    {
        await _sut.HandleAsync(new SetUpTotpCommand("  Work phone "), Ct);

        _added.ShouldHaveSingleItem().Label.ShouldBe("Work phone");
    }

    [Fact]
    public async Task Setup_requires_step_up()
    {
        _currentUser.AuthTime.Returns(Now - TimeSpan.FromMinutes(10));

        var result = await _sut.HandleAsync(new SetUpTotpCommand(null), Ct);

        result.Error.ShouldBe(MfaErrors.StepUpRequired);
        await _users.DidNotReceiveWithAnyArgs().GetByIdAsync(default, Ct);
        await _methods.DidNotReceiveWithAnyArgs().GetTotpAsync(default, Ct);
        _methods.DidNotReceiveWithAnyArgs().Add(default!);
        _auditEntries.ShouldBeEmpty();
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesUnlessDuplicateAsync(default!, Ct);
    }

    [Fact]
    public async Task Setup_while_confirmed_is_already_enabled()
    {
        var confirmed = UserMfaMethod.StartTotp(_user.Id, "Old phone", _protector.Protect(Totp.NewSecret()), Now.AddDays(-3));
        confirmed.Confirm(Totp.TimeStepAt(Now.AddDays(-3)), Now.AddDays(-3));
        var stored = confirmed.SecretEncrypted;
        _methods.GetTotpAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(confirmed);

        var result = await _sut.HandleAsync(new SetUpTotpCommand(null), Ct);

        result.Error.ShouldBe(MfaErrors.AlreadyEnabled);
        confirmed.SecretEncrypted.ShouldBe(stored);
        _methods.DidNotReceiveWithAnyArgs().Add(default!);
        _auditEntries.ShouldBeEmpty();
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesUnlessDuplicateAsync(default!, Ct);
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Setup_twice_replaces_the_pending_secret()
    {
        var firstSecret = Totp.NewSecret();
        var pending = UserMfaMethod.StartTotp(_user.Id, "Old phone", _protector.Protect(firstSecret), Now.AddMinutes(-5));
        _methods.GetTotpAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(pending);

        var result = await _sut.HandleAsync(new SetUpTotpCommand("New phone"), Ct);

        // The same row, in place (D18): a new secret, label and start; nothing added.
        result.Value.MethodId.ShouldBe(pending.Id);
        _methods.DidNotReceiveWithAnyArgs().Add(default!);
        pending.Label.ShouldBe("New phone");
        pending.CreatedAt.ShouldBe(Now);
        pending.IsConfirmed.ShouldBeFalse();
        var secret = _protector.Unprotect(pending.SecretEncrypted);
        secret.ShouldNotBe(firstSecret);
        result.Value.Secret.ShouldBe(Base32.Encode(secret));
        _auditEntries.ShouldHaveSingleItem().EventType.ShouldBe(AuthAuditEvents.MfaSetupStarted);
        await _unitOfWork.Received(1).SaveChangesUnlessDuplicateAsync(UniqueIndexNames.MfaMethodPerUserAndType, Ct);
    }

    [Fact]
    public async Task Lost_insert_race_takes_over_the_pending_row_of_the_winner()
    {
        // A simultaneous setup inserted the user's row first: the unique index refused this insert and nothing of it was saved.
        var winner = UserMfaMethod.StartTotp(_user.Id, "Other tab", _protector.Protect(Totp.NewSecret()), Now);
        _methods.GetTotpAsync(_user.Id, Arg.Any<CancellationToken>()).Returns((UserMfaMethod?)null, winner);
        _unitOfWork.SaveChangesUnlessDuplicateAsync(UniqueIndexNames.MfaMethodPerUserAndType, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.HandleAsync(new SetUpTotpCommand(null), Ct);

        // This request's secret replaces the winner's, so what it answers is what is stored (never a 500).
        result.Value.MethodId.ShouldBe(winner.Id);
        result.Value.Secret.ShouldBe(Base32.Encode(_protector.Unprotect(winner.SecretEncrypted)));
        _auditEntries.Count.ShouldBe(2);
        _auditEntries.ShouldAllBe(entry => entry.EventType == AuthAuditEvents.MfaSetupStarted);
        _auditEntries[1].Details.ShouldBe($$"""{"methodId":"{{winner.Id}}"}""");
        await _unitOfWork.Received(1).SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Lost_insert_race_against_a_confirmed_method_is_already_enabled()
    {
        var winner = UserMfaMethod.StartTotp(_user.Id, "Other tab", _protector.Protect(Totp.NewSecret()), Now);
        winner.Confirm(Totp.TimeStepAt(Now), Now);
        _methods.GetTotpAsync(_user.Id, Arg.Any<CancellationToken>()).Returns((UserMfaMethod?)null, winner);
        _unitOfWork.SaveChangesUnlessDuplicateAsync(UniqueIndexNames.MfaMethodPerUserAndType, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.HandleAsync(new SetUpTotpCommand(null), Ct);

        result.Error.ShouldBe(MfaErrors.AlreadyEnabled);
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("no-session")]
    [InlineData("unknown-user")]
    [InlineData("suspended")]
    [InlineData("unknown-session")]
    [InlineData("foreign-session")]
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
                _sessions.GetByIdAsync(_session.Id, Arg.Any<CancellationToken>()).Returns((UserSession?)null);
                break;
            case "foreign-session":
                _currentUser.UserId.Returns(Guid.NewGuid());
                _users.GetByIdAsync(Arg.Is<Guid>(id => id != _user.Id), Arg.Any<CancellationToken>()).Returns(_user);
                break;
            case "revoked-session":
                _session.Revoke(SessionRevokedReason.Logout, Now);
                break;
            default:
                _user.ChangeMfa(MfaChange.Disabled, Now);
                _user.ClearDomainEvents();
                break;
        }

        var result = await _sut.HandleAsync(new SetUpTotpCommand(null), Ct);

        result.Error.Type.ShouldBe(TemplateName.SharedKernel.ErrorType.NotFound);
        result.Error.Code.ShouldBe(UserErrors.NotFound(Guid.Empty).Code);
        _methods.DidNotReceiveWithAnyArgs().Add(default!);
        _auditEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Response_text_shows_only_the_method_id()
    {
        var response = (await _sut.HandleAsync(new SetUpTotpCommand(null), Ct)).Value;

        response.ToString().ShouldBe($"TotpSetupResponse {{ MethodId = {response.MethodId} }}");
        response.ToString().ShouldNotContain(response.Secret);
    }
}
