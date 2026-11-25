using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.IntegrationEvents;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Verification;
using TemplateName.Modules.Auth.Domain.Verification.Events;
using TemplateName.UnitTests.Application;

namespace TemplateName.UnitTests.Auth.IntegrationEvents;

public sealed class PhoneSignInCodeRequestedPublisherTests
{
    private const string Number = "+60123456789";

    private static readonly DateTimeOffset Now = new(2026, 11, 24, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset MessageOccurredAt = Now.AddSeconds(-5);
    private static readonly Guid MessageId = new("0199c8a0-0000-7000-8000-000000000042");

    private readonly IPhoneVerificationCodeRepository _codes = Substitute.For<IPhoneVerificationCodeRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IOutboxMessageContext _messageContext = Substitute.For<IOutboxMessageContext>();
    private readonly RecordingIntegrationEventPublisher _publisher = new();
    private readonly RecordingLogger<PublishPhoneSignInCodeIssuedDomainEventHandler> _logger = new();
    private readonly User _user = User.Register("alice@example.com", "Alice", "en", "hash", Now.AddDays(-1)).Value;
    private readonly PhoneVerificationCode _code;
    private readonly PhoneSignInCodeIssuedDomainEvent _issued;

    public PhoneSignInCodeRequestedPublisherTests()
    {
        _code = PhoneVerificationCode.Issue(_user.Id, PhoneCodePurpose.SignIn, Number, "042137", "ciphertext-of-the-code", TimeSpan.FromMinutes(5), Now.AddSeconds(-5));
        _issued = _code.DomainEvents.OfType<PhoneSignInCodeIssuedDomainEvent>().Single();
        _codes.GetByIdAsync(_code.Id, Arg.Any<CancellationToken>()).Returns(_code);
        _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_user);
        _messageContext.MessageId.Returns(MessageId);
        _messageContext.OccurredAt.Returns(MessageOccurredAt);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Copies_the_protected_code_unchanged()
    {
        await Handler().HandleAsync(_issued, Ct);

        var published = _publisher.Published.ShouldHaveSingleItem().ShouldBeOfType<PhoneSignInCodeRequestedIntegrationEvent>();
        published.ShouldBe(new PhoneSignInCodeRequestedIntegrationEvent(MessageId, MessageOccurredAt, _user.Id, Number, "ciphertext-of-the-code", _code.ExpiresAt));
        published.ToString().ShouldNotContain("60123456789");
        published.ToString().ShouldNotContain("ciphertext-of-the-code");
    }

    [Fact]
    public async Task Skips_a_code_no_longer_pending()
    {
        _code.Invalidate(Now.AddSeconds(-1));

        await Handler().HandleAsync(_issued, Ct);

        _publisher.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task Skips_a_code_of_a_missing_user()
    {
        _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns((User?)null);

        await Handler().HandleAsync(_issued, Ct);

        _publisher.Published.ShouldBeEmpty();
    }

    private PublishPhoneSignInCodeIssuedDomainEventHandler Handler()
        => new(_codes, _users, _publisher, _messageContext, new FakeTimeProvider(Now), _logger);
}
