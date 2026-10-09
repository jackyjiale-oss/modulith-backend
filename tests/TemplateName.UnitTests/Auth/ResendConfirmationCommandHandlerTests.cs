using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Application.Common.Security;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Registration.ResendConfirmation;
using TemplateName.Modules.Auth.Application.Verification;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Verification;
using TemplateName.Modules.Auth.Domain.Verification.Events;

namespace TemplateName.UnitTests.Auth;

public sealed class ResendConfirmationCommandHandlerTests
{
    private const string TokenValue = "new-token";

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
    private readonly User _user = User.Register("Alice@Example.com", "Alice", "en", "hash", Now.AddHours(-1)).Value;
    private readonly ResendConfirmationCommandHandler _sut;

    public ResendConfirmationCommandHandlerTests()
    {
        _tokenService.Generate().Returns(new GeneratedToken(TokenValue, TokenHash));
        _secretProtector.Protect(Arg.Any<string>()).Returns(call => "protected:" + call.Arg<string>());
        _auditWriter.Record(Arg.Do<AuthAuditLog>(_auditEntries.Add));
        _users.GetByNormalizedEmailAsync("ALICE@EXAMPLE.COM", Arg.Any<CancellationToken>()).Returns(_user);
        _sut = new ResendConfirmationCommandHandler(
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
    public async Task Resend_inside_cooldown_issues_nothing()
    {
        _verificationCodes.GetLastIssuedAtAsync(_user.Id, VerificationPurpose.EmailVerify, Arg.Any<CancellationToken>())
            .Returns(Now.AddSeconds(-59).UtcDateTime);

        var result = await _sut.HandleAsync(new ResendConfirmationCommand("alice@example.com"), Ct);

        result.IsSuccess.ShouldBeTrue();
        await AssertNothingIssuedAsync();
    }

    [Fact]
    public async Task Unknown_email_succeeds_and_issues_nothing()
    {
        var result = await _sut.HandleAsync(new ResendConfirmationCommand("nobody@example.com"), Ct);

        result.IsSuccess.ShouldBeTrue();
        await AssertNothingIssuedAsync();
    }

    [Fact]
    public async Task Confirmed_user_succeeds_and_issues_nothing()
    {
        _user.ConfirmEmail(Now);

        var result = await _sut.HandleAsync(new ResendConfirmationCommand("alice@example.com"), Ct);

        result.IsSuccess.ShouldBeTrue();
        await AssertNothingIssuedAsync();
    }

    [Fact]
    public async Task Resend_after_cooldown_invalidates_pending_codes_and_issues_a_new_one()
    {
        var previous = VerificationCode.Issue(_user.Id, VerificationPurpose.EmailVerify, _user.NormalizedEmail, new byte[32], "old", TimeSpan.FromHours(1), null, Now.AddMinutes(-1));
        _verificationCodes.GetLastIssuedAtAsync(_user.Id, VerificationPurpose.EmailVerify, Arg.Any<CancellationToken>())
            .Returns(Now.AddMinutes(-1).UtcDateTime);
        _verificationCodes.GetPendingAsync(_user.Id, VerificationPurpose.EmailVerify, Now, Arg.Any<CancellationToken>())
            .Returns([previous]);
        VerificationCode? issued = null;
        _verificationCodes.Add(Arg.Do<VerificationCode>(code => issued = code));

        var result = await _sut.HandleAsync(new ResendConfirmationCommand(" ALICE@example.com "), Ct);

        result.IsSuccess.ShouldBeTrue();
        previous.InvalidatedAt.ShouldBe(Now);
        issued.ShouldNotBeNull();
        issued.UserId.ShouldBe(_user.Id);
        issued.Purpose.ShouldBe(VerificationPurpose.EmailVerify);
        issued.TokenHash.ShouldBe(TokenHash);
        issued.ExpiresAt.ShouldBe(Now.AddHours(1));
        issued.DomainEvents.OfType<VerificationCodeIssuedDomainEvent>().ShouldHaveSingleItem().ProtectedToken.ShouldBe("protected:" + TokenValue);
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.ConfirmationResent);
        audit.Succeeded.ShouldBeTrue();
        audit.UserId.ShouldBe(_user.Id);
        await _unitOfWork.Received(1).SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task User_without_any_code_gets_one()
    {
        var result = await _sut.HandleAsync(new ResendConfirmationCommand("alice@example.com"), Ct);

        result.IsSuccess.ShouldBeTrue();
        _verificationCodes.ReceivedWithAnyArgs(1).Add(default!);
        await _unitOfWork.Received(1).SaveChangesAsync(Ct);
    }

    private async Task AssertNothingIssuedAsync()
    {
        _verificationCodes.DidNotReceiveWithAnyArgs().Add(default!);
        _tokenService.DidNotReceive().Generate();
        _auditEntries.ShouldBeEmpty();
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }
}
