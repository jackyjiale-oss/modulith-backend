using NSubstitute;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Verification;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Users.Events;
using TemplateName.UnitTests.Application;

namespace TemplateName.UnitTests.Auth;

public sealed class SendRegistrationAttemptedEmailDomainEventHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IEmailSender _emailSender = Substitute.For<IEmailSender>();
    private readonly List<EmailMessage> _sent = [];
    private readonly RecordingLogger<SendRegistrationAttemptedEmailDomainEventHandler> _logger = new();
    private readonly User _user = User.Register("Alice@Example.com", "Alice <b>", "ms", "hash", Now).Value;
    private readonly SendRegistrationAttemptedEmailDomainEventHandler _sut;

    public SendRegistrationAttemptedEmailDomainEventHandlerTests()
    {
        _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_user);
        _emailSender.SendAsync(Arg.Do<EmailMessage>(_sent.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _sut = new SendRegistrationAttemptedEmailDomainEventHandler(_users, _emailSender, _logger);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Registration_attempt_sends_the_notice_without_a_link()
    {
        await _sut.HandleAsync(new RegistrationAttemptedDomainEvent(_user.Id, _user.Email, _user.Locale), Ct);

        _sent.ShouldHaveSingleItem().ShouldBe(AuthEmails.RegistrationAttempted("Alice@Example.com", "Alice <b>"));
        AssertNoAddressLogged();
    }

    [Fact]
    public async Task Registration_attempt_for_a_missing_user_sends_nothing()
    {
        var userId = Guid.NewGuid();

        await _sut.HandleAsync(new RegistrationAttemptedDomainEvent(userId, "gone@example.com", "en"), Ct);

        _sent.ShouldBeEmpty();
        _logger.Entries.ShouldHaveSingleItem().Message.ShouldContain(userId.ToString());
        AssertNoAddressLogged();
    }

    private void AssertNoAddressLogged()
    {
        foreach (var entry in _logger.Entries)
        {
            (entry.Message + string.Join(' ', entry.Properties.Values)).ShouldNotContain("example.com", Case.Insensitive);
        }
    }
}
