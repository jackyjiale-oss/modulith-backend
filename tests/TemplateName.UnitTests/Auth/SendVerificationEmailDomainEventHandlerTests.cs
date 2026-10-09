using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using TemplateName.Application.Common.Security;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Verification;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Verification;
using TemplateName.Modules.Auth.Domain.Verification.Events;
using TemplateName.UnitTests.Application;

namespace TemplateName.UnitTests.Auth;

public sealed class SendVerificationEmailDomainEventHandlerTests
{
    private const string Token = "a+token/with=specials";
    private const string ProtectedToken = "protected-token";

    private static readonly DateTimeOffset Now = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly ISecretProtector _secretProtector = Substitute.For<ISecretProtector>();
    private readonly IEmailSender _emailSender = Substitute.For<IEmailSender>();
    private readonly List<EmailMessage> _sent = [];
    private readonly LinksOptions _links = new()
    {
        ConfirmEmailUrl = "https://app.example.com/confirm?token={token}",
        ResetPasswordUrl = "https://app.example.com/reset?token={token}",
    };

    private readonly User _user = User.Register("Alice@Example.com", "Alice <b>", "ms", "hash", Now).Value;

    public SendVerificationEmailDomainEventHandlerTests()
    {
        _secretProtector.Unprotect(ProtectedToken).Returns(Token);
        _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_user);
        _emailSender.SendAsync(Arg.Do<EmailMessage>(_sent.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Email_verify_code_sends_the_confirmation_email_with_the_decrypted_token()
    {
        var logger = new RecordingLogger<SendVerificationEmailDomainEventHandler>();

        await VerificationHandler(logger).HandleAsync(Issued(VerificationPurpose.EmailVerify), Ct);

        var message = _sent.ShouldHaveSingleItem();
        var expected = AuthEmails.ConfirmEmail("Alice@Example.com", "Alice <b>", _links.ConfirmEmailLink(Token));
        message.ShouldBe(expected);
        message.TextBody.ShouldContain("https://app.example.com/confirm?token=" + Uri.EscapeDataString(Token));
        AssertNothingSensitiveLogged(logger.Entries);
    }

    [Fact]
    public async Task Password_reset_code_sends_the_reset_email()
    {
        var logger = new RecordingLogger<SendVerificationEmailDomainEventHandler>();

        await VerificationHandler(logger).HandleAsync(Issued(VerificationPurpose.PasswordReset), Ct);

        _sent.ShouldHaveSingleItem().ShouldBe(AuthEmails.ResetPassword("Alice@Example.com", "Alice <b>", _links.ResetPasswordLink(Token)));
        AssertNothingSensitiveLogged(logger.Entries);
    }

    [Fact]
    public async Task Missing_user_is_logged_and_nothing_is_sent()
    {
        var logger = new RecordingLogger<SendVerificationEmailDomainEventHandler>();
        var issued = Issued(VerificationPurpose.EmailVerify) with { UserId = Guid.NewGuid() };

        await VerificationHandler(logger).HandleAsync(issued, Ct);

        _sent.ShouldBeEmpty();
        _secretProtector.DidNotReceiveWithAnyArgs().Unprotect(default!);
        logger.Entries.ShouldHaveSingleItem().Message.ShouldContain(issued.CodeId.ToString());
        AssertNothingSensitiveLogged(logger.Entries);
    }

    [Fact]
    public async Task Code_for_an_address_the_user_no_longer_has_is_not_sent()
    {
        var logger = new RecordingLogger<SendVerificationEmailDomainEventHandler>();
        var issued = Issued(VerificationPurpose.EmailVerify) with { Target = "OLD@EXAMPLE.COM" };

        await VerificationHandler(logger).HandleAsync(issued, Ct);

        _sent.ShouldBeEmpty();
        logger.Entries.ShouldHaveSingleItem();
        AssertNothingSensitiveLogged(logger.Entries);
    }

    private static void AssertNothingSensitiveLogged(IEnumerable<(LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Properties)> entries)
    {
        foreach (var entry in entries)
        {
            var text = entry.Message + string.Join(' ', entry.Properties.Values);
            text.ShouldNotContain(Token);
            text.ShouldNotContain(Uri.EscapeDataString(Token));
            text.ShouldNotContain(ProtectedToken);
            text.ShouldNotContain("example.com", Case.Insensitive);
        }
    }

    private SendVerificationEmailDomainEventHandler VerificationHandler(ILogger<SendVerificationEmailDomainEventHandler> logger)
        => new(_users, _secretProtector, _emailSender, Options.Create(_links), logger);

    private VerificationCodeIssuedDomainEvent Issued(VerificationPurpose purpose)
        => new(Guid.NewGuid(), _user.Id, purpose, _user.NormalizedEmail, ProtectedToken);
}
