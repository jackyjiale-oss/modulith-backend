using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Registration.ConfirmEmail;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Verification;

namespace TemplateName.UnitTests.Auth;

public sealed class ConfirmEmailCommandHandlerTests
{
    private const string Token = "presented-token";

    private static readonly DateTimeOffset Now = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);
    private static readonly byte[] TokenHash = [.. Enumerable.Range(1, 32).Select(value => (byte)value)];

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IVerificationCodeRepository _verificationCodes = Substitute.For<IVerificationCodeRepository>();
    private readonly ISecureTokenService _tokenService = Substitute.For<ISecureTokenService>();
    private readonly IAuthAuditWriter _auditWriter = Substitute.For<IAuthAuditWriter>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly FakeTimeProvider _timeProvider = new(Now);
    private readonly List<AuthAuditLog> _auditEntries = [];
    private readonly User _user = User.Register("alice@example.com", "Alice", "en", "hash", Now.AddMinutes(-5)).Value;
    private readonly ConfirmEmailCommandHandler _sut;

    public ConfirmEmailCommandHandlerTests()
    {
        _tokenService.Hash(Token).Returns(TokenHash);
        _auditWriter.Record(Arg.Do<AuthAuditLog>(_auditEntries.Add));
        _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_user);
        _verificationCodes.TryConsumeAsync(Arg.Any<Guid>(), Arg.Any<VerificationPurpose>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _sut = new ConfirmEmailCommandHandler(_users, _verificationCodes, _tokenService, _auditWriter, _unitOfWork, _timeProvider);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Confirm_with_unknown_token_returns_invalid_token()
    {
        var result = await _sut.HandleAsync(new ConfirmEmailCommand(Token), Ct);

        result.Error.ShouldBe(VerificationErrors.InvalidToken);
        await _users.DidNotReceiveWithAnyArgs().GetByIdAsync(default, Ct);
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.EmailConfirmFailed);
        audit.Succeeded.ShouldBeFalse();
        audit.FailureReason.ShouldBe(VerificationErrors.InvalidToken.Code);
        audit.UserId.ShouldBeNull();
        audit.AttemptedIdentifier.ShouldBeNull();
        audit.Details.ShouldBeNull();
        await _unitOfWork.Received(1).SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Valid_token_confirms_the_email_consumes_the_code_and_audits()
    {
        var code = IssueCode(VerificationPurpose.EmailVerify, Now.AddMinutes(-1));

        var result = await _sut.HandleAsync(new ConfirmEmailCommand(Token), Ct);

        result.IsSuccess.ShouldBeTrue();
        _user.EmailConfirmed.ShouldBeTrue();
        code.ConsumedAt.ShouldBe(Now);
        await _verificationCodes.Received(1).TryConsumeAsync(code.Id, VerificationPurpose.EmailVerify, Now, CancellationToken.None);
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.EmailConfirmed);
        audit.Succeeded.ShouldBeTrue();
        audit.UserId.ShouldBe(_user.Id);

        // The claim has committed, so the confirmation is saved even when the client goes away.
        await _unitOfWork.Received(1).SaveChangesAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Claim_lost_to_a_simultaneous_confirm_returns_invalid_token()
    {
        var code = IssueCode(VerificationPurpose.EmailVerify, Now.AddMinutes(-1));
        _verificationCodes.TryConsumeAsync(code.Id, VerificationPurpose.EmailVerify, Now, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.HandleAsync(new ConfirmEmailCommand(Token), Ct);

        result.Error.ShouldBe(VerificationErrors.InvalidToken);
        code.ConsumedAt.ShouldBeNull();
        _user.EmailConfirmed.ShouldBeFalse();
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.EmailConfirmFailed);
        audit.UserId.ShouldBe(_user.Id);
    }

    [Fact]
    public async Task Token_of_another_purpose_is_invalid_and_changes_nothing()
    {
        var code = IssueCode(VerificationPurpose.PasswordReset, Now.AddMinutes(-1));

        var result = await _sut.HandleAsync(new ConfirmEmailCommand(Token), Ct);

        result.Error.ShouldBe(VerificationErrors.InvalidToken);
        code.ConsumedAt.ShouldBeNull();
        _user.EmailConfirmed.ShouldBeFalse();
        await _users.DidNotReceiveWithAnyArgs().GetByIdAsync(default, Ct);
        await _verificationCodes.DidNotReceiveWithAnyArgs().TryConsumeAsync(default, default, default, Ct);
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.EmailConfirmFailed);
        audit.UserId.ShouldBe(_user.Id);
        await _unitOfWork.Received(1).SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Expired_token_is_invalid()
    {
        IssueCode(VerificationPurpose.EmailVerify, Now.AddHours(-1));

        var result = await _sut.HandleAsync(new ConfirmEmailCommand(Token), Ct);

        result.Error.ShouldBe(VerificationErrors.InvalidToken);
        _user.EmailConfirmed.ShouldBeFalse();
    }

    [Fact]
    public async Task Code_whose_user_no_longer_exists_is_invalid()
    {
        var code = VerificationCode.Issue(Guid.NewGuid(), VerificationPurpose.EmailVerify, VerificationTrigger.SelfService, "GONE@EXAMPLE.COM", TokenHash, "protected", TimeSpan.FromHours(1), null, Now.AddMinutes(-1));
        _verificationCodes.GetByTokenHashAsync(TokenHash, Arg.Any<CancellationToken>()).Returns(code);

        var result = await _sut.HandleAsync(new ConfirmEmailCommand(Token), Ct);

        result.Error.ShouldBe(VerificationErrors.InvalidToken);
        _auditEntries.ShouldHaveSingleItem().EventType.ShouldBe(AuthAuditEvents.EmailConfirmFailed);
    }

    [Fact]
    public void Command_text_never_shows_the_token()
    {
        new ConfirmEmailCommand(Token).ToString().ShouldNotContain(Token);
    }

    private VerificationCode IssueCode(VerificationPurpose purpose, DateTimeOffset issuedAt)
    {
        var code = VerificationCode.Issue(_user.Id, purpose, VerificationTrigger.SelfService, _user.NormalizedEmail, TokenHash, "protected", TimeSpan.FromHours(1), null, issuedAt);
        _verificationCodes.GetByTokenHashAsync(TokenHash, Arg.Any<CancellationToken>()).Returns(code);
        return code;
    }
}
