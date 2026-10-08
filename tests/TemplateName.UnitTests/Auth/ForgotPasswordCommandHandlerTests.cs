using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Passwords.Forgot;
using TemplateName.Modules.Auth.Application.Verification;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Verification;
using TemplateName.Modules.Auth.Domain.Verification.Events;

namespace TemplateName.UnitTests.Auth;

public sealed class ForgotPasswordCommandHandlerTests
{
    private const string TokenValue = "new-reset-token";

    private static readonly DateTimeOffset Now = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);
    private static readonly byte[] TokenHash = [.. Enumerable.Range(1, 32).Select(value => (byte)value)];

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IVerificationCodeRepository _verificationCodes = Substitute.For<IVerificationCodeRepository>();
    private readonly ISecureTokenService _tokenService = Substitute.For<ISecureTokenService>();
    private readonly ISecretProtector _secretProtector = Substitute.For<ISecretProtector>();
    private readonly IAuthAuditWriter _auditWriter = Substitute.For<IAuthAuditWriter>();
    private readonly IClientContext _clientContext = Substitute.For<IClientContext>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly List<AuthAuditLog> _auditEntries = [];
    private readonly User _user = User.Register("Alice@Example.com", "Alice", "en", "hash", Now.AddDays(-1)).Value;
    private readonly ForgotPasswordCommandHandler _sut;

    public ForgotPasswordCommandHandlerTests()
    {
        _tokenService.Generate().Returns(new GeneratedToken(TokenValue, TokenHash));
        _secretProtector.Protect(Arg.Any<string>()).Returns(call => "protected:" + call.Arg<string>());
        _clientContext.IpAddress.Returns("203.0.113.7");
        _auditWriter.Record(Arg.Do<AuthAuditLog>(_auditEntries.Add));
        _users.GetByNormalizedEmailAsync("ALICE@EXAMPLE.COM", Arg.Any<CancellationToken>()).Returns(_user);
        _sut = new ForgotPasswordCommandHandler(
            _users,
            _verificationCodes,
            _tokenService,
            _secretProtector,
            _auditWriter,
            _clientContext,
            _unitOfWork,
            Options.Create(new VerificationOptions()),
            new FakeTimeProvider(Now));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Forgot_for_unknown_email_issues_no_code()
    {
        var result = await _sut.HandleAsync(new ForgotPasswordCommand("nobody@example.com"), Ct);

        result.IsSuccess.ShouldBeTrue();
        _verificationCodes.DidNotReceiveWithAnyArgs().Add(default!);
        _tokenService.DidNotReceive().Generate();

        // The same amount of saving as a known address: one audit row, masked, without a user.
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.PasswordForgotRequested);
        audit.Succeeded.ShouldBeFalse();
        audit.UserId.ShouldBeNull();
        audit.FailureReason.ShouldBe(ForgotPasswordCommandHandler.UnknownEmailReason);
        audit.AttemptedIdentifier.ShouldBe("n****@example.com");
        await _unitOfWork.Received(1).SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Known_email_invalidates_pending_reset_codes_and_issues_a_new_one()
    {
        var previous = VerificationCode.Issue(_user.Id, VerificationPurpose.PasswordReset, _user.NormalizedEmail, new byte[32], "old", TimeSpan.FromMinutes(30), null, Now.AddMinutes(-5));
        _verificationCodes.GetLastIssuedAtAsync(_user.Id, VerificationPurpose.PasswordReset, Arg.Any<CancellationToken>())
            .Returns(Now.AddMinutes(-5).UtcDateTime);
        _verificationCodes.GetPendingAsync(_user.Id, VerificationPurpose.PasswordReset, Now, Arg.Any<CancellationToken>())
            .Returns([previous]);
        VerificationCode? issued = null;
        _verificationCodes.Add(Arg.Do<VerificationCode>(code => issued = code));

        var result = await _sut.HandleAsync(new ForgotPasswordCommand(" ALICE@example.com "), Ct);

        result.IsSuccess.ShouldBeTrue();
        previous.InvalidatedAt.ShouldBe(Now);
        issued.ShouldNotBeNull();
        issued.UserId.ShouldBe(_user.Id);
        issued.Purpose.ShouldBe(VerificationPurpose.PasswordReset);
        issued.Target.ShouldBe("ALICE@EXAMPLE.COM");
        issued.TokenHash.ShouldBe(TokenHash);
        issued.ExpiresAt.ShouldBe(Now.AddMinutes(30));
        issued.CreatedIp.ShouldBe("203.0.113.7");
        issued.DomainEvents.OfType<VerificationCodeIssuedDomainEvent>().ShouldHaveSingleItem().ProtectedToken.ShouldBe("protected:" + TokenValue);
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.PasswordForgotRequested);
        audit.Succeeded.ShouldBeTrue();
        audit.UserId.ShouldBe(_user.Id);
        audit.AttemptedIdentifier.ShouldBe("A****@example.com");
        await _unitOfWork.Received(1).SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Forgot_inside_cooldown_issues_nothing()
    {
        _verificationCodes.GetLastIssuedAtAsync(_user.Id, VerificationPurpose.PasswordReset, Arg.Any<CancellationToken>())
            .Returns(Now.AddSeconds(-59).UtcDateTime);

        var result = await _sut.HandleAsync(new ForgotPasswordCommand("alice@example.com"), Ct);

        result.IsSuccess.ShouldBeTrue();
        _verificationCodes.DidNotReceiveWithAnyArgs().Add(default!);
        await _verificationCodes.DidNotReceiveWithAnyArgs().GetPendingAsync(default, default, default, Ct);
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.Succeeded.ShouldBeFalse();
        audit.UserId.ShouldBe(_user.Id);
        audit.FailureReason.ShouldBe(ForgotPasswordCommandHandler.CooldownReason);
        await _unitOfWork.Received(1).SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Cooldown_counts_only_reset_codes()
    {
        _verificationCodes.GetLastIssuedAtAsync(_user.Id, VerificationPurpose.EmailVerify, Arg.Any<CancellationToken>())
            .Returns(Now.AddSeconds(-1).UtcDateTime);

        await _sut.HandleAsync(new ForgotPasswordCommand("alice@example.com"), Ct);

        _verificationCodes.ReceivedWithAnyArgs(1).Add(default!);
    }

    [Fact]
    public async Task User_without_a_password_gets_a_reset_code()
    {
        // An administrator-created account sets its first password through the reset link (Ruling R3: known for forgot).
        var invited = User.Register("invited@example.com", "Invited", "en", passwordHash: null, Now.AddDays(-1)).Value;
        _users.GetByNormalizedEmailAsync("INVITED@EXAMPLE.COM", Arg.Any<CancellationToken>()).Returns(invited);

        await _sut.HandleAsync(new ForgotPasswordCommand("invited@example.com"), Ct);

        _verificationCodes.Received(1).Add(Arg.Is<VerificationCode>(code => code.UserId == invited.Id && code.Purpose == VerificationPurpose.PasswordReset));
        _auditEntries.ShouldHaveSingleItem().Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task Suspended_user_gets_no_code()
    {
        _user.Suspend(Now);

        var result = await _sut.HandleAsync(new ForgotPasswordCommand("alice@example.com"), Ct);

        result.IsSuccess.ShouldBeTrue();
        _verificationCodes.DidNotReceiveWithAnyArgs().Add(default!);
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.Succeeded.ShouldBeFalse();
        audit.UserId.ShouldBe(_user.Id);
        audit.FailureReason.ShouldBe(UserErrors.AccountInactive.Code);
        await _unitOfWork.Received(1).SaveChangesAsync(Ct);
    }
}
