using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Application.Common.Security;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Phone;
using TemplateName.Modules.Auth.Application.PhoneSignIn;
using TemplateName.Modules.Auth.Application.PhoneSignIn.Send;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Verification;
using TemplateName.Modules.Auth.Domain.Verification.Events;

namespace TemplateName.UnitTests.Auth.PhoneSignIn;

public sealed class SendPhoneSignInCodeCommandHandlerTests
{
    private const string Number = "+60123456789";

    private static readonly DateTimeOffset Now = new(2026, 11, 24, 8, 0, 0, TimeSpan.Zero);

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IPhoneVerificationCodeRepository _codes = Substitute.For<IPhoneVerificationCodeRepository>();
    private readonly IRoleRepository _roles = Substitute.For<IRoleRepository>();
    private readonly IMfaMethodRepository _mfaMethods = Substitute.For<IMfaMethodRepository>();
    private readonly ISecretProtector _secretProtector = Substitute.For<ISecretProtector>();
    private readonly IAuthAuditWriter _auditWriter = Substitute.For<IAuthAuditWriter>();
    private readonly IAuthMetrics _metrics = Substitute.For<IAuthMetrics>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly List<AuthAuditLog> _auditEntries = [];
    private readonly List<PhoneVerificationCode> _issued = [];
    private readonly User _user = User.Register("alice@example.com", "Alice", "en", "hash", Now.AddDays(-30)).Value;
    private readonly PhoneOptions _options = new() { Enabled = true, SignInEnabled = true };
    private readonly SendPhoneSignInCodeCommandHandler _sut;

    public SendPhoneSignInCodeCommandHandlerTests()
    {
        _user.VerifyPhoneNumber(Number, Now.AddDays(-2));
        _user.ClearDomainEvents();
        _users.GetByPhoneNumberAsync(Number, Arg.Any<CancellationToken>()).Returns(_user);
        _codes.ListPendingAsync(_user.Id, Arg.Any<PhoneCodePurpose?>(), Now, Arg.Any<CancellationToken>()).Returns([]);
        _codes.ListIssuedSinceAsync(_user.Id, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns([]);
        _codes.Add(Arg.Do<PhoneVerificationCode>(_issued.Add));
        _secretProtector.Protect(Arg.Any<string>()).Returns(call => "protected:" + call.Arg<string>());
        _unitOfWork.TrySaveChangesAsync(Arg.Any<CancellationToken>()).Returns(true);
        _auditWriter.Record(Arg.Do<AuthAuditLog>(_auditEntries.Add));
        var options = Options.Create(_options);
        _sut = new SendPhoneSignInCodeCommandHandler(
            _users,
            _codes,
            new PhoneSignInGate(_roles, _mfaMethods, options),
            new PhoneCodeBudget(_codes),
            _secretProtector,
            _auditWriter,
            _metrics,
            _unitOfWork,
            options,
            new FakeTimeProvider(Now));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Issues_a_sign_in_code_for_the_verified_number()
    {
        var result = await _sut.HandleAsync(new SendPhoneSignInCodeCommand(Number), Ct);

        result.IsSuccess.ShouldBeTrue();
        var code = _issued.ShouldHaveSingleItem();
        code.Purpose.ShouldBe(PhoneCodePurpose.SignIn);
        code.PhoneNumber.ShouldBe(Number);
        code.ExpiresAt.ShouldBe(Now.AddMinutes(5));
        var issued = code.DomainEvents.OfType<PhoneSignInCodeIssuedDomainEvent>().ShouldHaveSingleItem();
        code.Matches(issued.ProtectedCode["protected:".Length..]).ShouldBeTrue();
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.PhoneSignInRequested);
        audit.Succeeded.ShouldBeTrue();
        audit.UserId.ShouldBe(_user.Id);
        await _unitOfWork.Received(1).TrySaveChangesAsync(Arg.Any<CancellationToken>());
        _metrics.Received(1).RecordPhoneSignIn(PhoneSignInOutcome.Sent);
    }

    [Fact]
    public async Task Sets_PhoneCodeRequestedAt()
    {
        await _sut.HandleAsync(new SendPhoneSignInCodeCommand(Number), Ct);

        _user.PhoneCodeRequestedAt.ShouldBe(Now);
    }

    [Fact]
    public async Task Issues_a_sign_in_code_and_ends_pending_sign_in_codes_only()
    {
        var previous = PhoneVerificationCode.Issue(_user.Id, PhoneCodePurpose.SignIn, Number, "111111", "protected", TimeSpan.FromMinutes(5), Now.AddMinutes(-3));
        var verify = PhoneVerificationCode.Issue(_user.Id, PhoneCodePurpose.Verify, "+60198765432", "222222", "protected", TimeSpan.FromMinutes(10), Now.AddMinutes(-4));
        _codes.ListPendingAsync(_user.Id, PhoneCodePurpose.SignIn, Now, Arg.Any<CancellationToken>()).Returns([previous]);
        _codes.ListPendingAsync(_user.Id, PhoneCodePurpose.Verify, Now, Arg.Any<CancellationToken>()).Returns([verify]);
        _codes.ListPendingAsync(_user.Id, null, Now, Arg.Any<CancellationToken>()).Returns([previous, verify]);

        await _sut.HandleAsync(new SendPhoneSignInCodeCommand(Number), Ct);

        previous.InvalidatedAt.ShouldBe(Now);
        verify.InvalidatedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Formatting_is_normalized_at_send()
    {
        (await _sut.HandleAsync(new SendPhoneSignInCodeCommand("+60 12-345 6789"), Ct)).IsSuccess.ShouldBeTrue();

        _issued.ShouldHaveSingleItem().PhoneNumber.ShouldBe(Number);
    }

    [Theory]
    [InlineData("0123456789")]
    [InlineData("+60321234567")]
    [InlineData("+abc")]
    public async Task An_invalid_number_is_invalid_phone_number(string number)
    {
        (await _sut.HandleAsync(new SendPhoneSignInCodeCommand(number), Ct)).Error.ShouldBe(PhoneErrors.InvalidPhoneNumber);

        await _users.DidNotReceiveWithAnyArgs().GetByPhoneNumberAsync(default!, Ct);
    }

    [Fact]
    public async Task Unknown_number_saves_nothing()
    {
        _users.GetByPhoneNumberAsync(Number, Arg.Any<CancellationToken>()).Returns((User?)null);

        (await _sut.HandleAsync(new SendPhoneSignInCodeCommand(Number), Ct)).IsSuccess.ShouldBeTrue();

        _issued.ShouldBeEmpty();
        _auditEntries.ShouldBeEmpty();
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
        await _unitOfWork.DidNotReceiveWithAnyArgs().TrySaveChangesAsync(Ct);
        _metrics.Received(1).RecordPhoneSignIn(PhoneSignInOutcome.Suppressed);
    }

    [Fact]
    public async Task Suspended_is_silent_and_audited_inactive()
    {
        _user.Suspend(Now.AddDays(-1));

        await AssertSilentAsync("inactive");
    }

    [Fact]
    public async Task Locked_is_silent_and_audited_locked()
    {
        _user.RecordFailedSignIn(Now.AddMinutes(-1), maxFailedAttempts: 1, TimeSpan.FromMinutes(15));

        await AssertSilentAsync("locked");
    }

    [Fact]
    public async Task Denied_role_is_silent_and_audited_role_denied()
    {
        var admin = Role.CreateSystem("Admin", "Administrators", Now.AddDays(-30));
        _user.AssignRole(admin.Id, assignedBy: null, Now.AddDays(-1));
        _roles.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns([admin]);

        await AssertSilentAsync("role_denied");
    }

    [Fact]
    public async Task Cooldown_is_silent_and_audited_cooldown()
    {
        _codes.GetLastIssuedAtAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(Now.AddSeconds(-30));

        await AssertSilentAsync("cooldown");
    }

    [Fact]
    public async Task Daily_cap_is_silent_and_audited_daily_cap()
    {
        _codes.ListIssuedSinceAsync(_user.Id, Now.AddDays(-1), Arg.Any<CancellationToken>())
            .Returns([Now.AddHours(-20), Now.AddHours(-10), Now.AddHours(-5), Now.AddHours(-2), Now.AddHours(-1)]);

        await AssertSilentAsync("daily_cap");
    }

    [Fact]
    public async Task A_lost_row_version_race_is_still_success()
    {
        _unitOfWork.TrySaveChangesAsync(Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.HandleAsync(new SendPhoneSignInCodeCommand(Number), Ct);

        result.IsSuccess.ShouldBeTrue();
        _metrics.Received(1).RecordPhoneSignIn(PhoneSignInOutcome.Suppressed);
        _metrics.DidNotReceive().RecordPhoneSignIn(PhoneSignInOutcome.Sent);
    }

    [Fact]
    public void The_command_never_prints_the_number() =>
        new SendPhoneSignInCodeCommand(Number).ToString().ShouldNotContain("60123456789");

    private async Task AssertSilentAsync(string reason)
    {
        var result = await _sut.HandleAsync(new SendPhoneSignInCodeCommand(Number), Ct);

        result.IsSuccess.ShouldBeTrue();
        _issued.ShouldBeEmpty();
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.PhoneSignInRequested);
        audit.Succeeded.ShouldBeFalse();
        audit.FailureReason.ShouldBe(reason);
        audit.UserId.ShouldBe(_user.Id);
        _user.PhoneCodeRequestedAt.ShouldBeNull();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        _metrics.Received(1).RecordPhoneSignIn(PhoneSignInOutcome.Suppressed);
    }
}
