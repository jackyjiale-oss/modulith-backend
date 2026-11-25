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

public sealed class PhoneVerificationRequestedPublisherTests
{
    private const string Number = "+60123456789";

    private static readonly DateTimeOffset Now = new(2026, 11, 24, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset MessageOccurredAt = Now.AddSeconds(-5);
    private static readonly Guid MessageId = new("0199c8a0-0000-7000-8000-000000000002");

    private readonly IPhoneVerificationCodeRepository _codes = Substitute.For<IPhoneVerificationCodeRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IOutboxMessageContext _messageContext = Substitute.For<IOutboxMessageContext>();
    private readonly RecordingIntegrationEventPublisher _publisher = new();
    private readonly RecordingLogger<PublishPhoneVerificationRequestedDomainEventHandler> _logger = new();
    private readonly User _user = User.Register("alice@example.com", "Alice", "en", "hash", Now.AddDays(-1)).Value;
    private readonly PhoneVerificationCode _code;
    private readonly PhoneVerificationRequestedDomainEvent _requested;

    public PhoneVerificationRequestedPublisherTests()
    {
        _code = PhoneVerificationCode.Issue(_user.Id, PhoneCodePurpose.Verify, Number, "042137", "ciphertext-of-the-code", TimeSpan.FromMinutes(10), Now.AddSeconds(-5));
        _requested = _code.DomainEvents.OfType<PhoneVerificationRequestedDomainEvent>().Single();
        _codes.GetByIdAsync(_code.Id, Arg.Any<CancellationToken>()).Returns(_code);
        _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_user);
        _messageContext.MessageId.Returns(MessageId);
        _messageContext.OccurredAt.Returns(MessageOccurredAt);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Publishes_the_pending_number_and_the_protected_code_unchanged()
    {
        await Handler().HandleAsync(_requested, Ct);

        var published = _publisher.Published.ShouldHaveSingleItem().ShouldBeOfType<PhoneVerificationRequestedIntegrationEvent>();
        published.Id.ShouldBe(MessageId);
        published.OccurredAt.ShouldBe(MessageOccurredAt);
        published.UserId.ShouldBe(_user.Id);
        published.PhoneNumber.ShouldBe(Number);
        published.ProtectedCode.ShouldBe("ciphertext-of-the-code");
        published.ExpiresAt.ShouldBe(_code.ExpiresAt);
        published.ToString().ShouldNotContain("60123456789");
        published.ToString().ShouldNotContain("ciphertext-of-the-code");
    }

    [Fact]
    public async Task A_code_that_is_no_longer_pending_is_not_sent()
    {
        _code.Invalidate(Now.AddSeconds(-1));

        await Handler().HandleAsync(_requested, Ct);

        _publisher.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_code_of_a_missing_user_is_not_sent()
    {
        _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns((User?)null);

        await Handler().HandleAsync(_requested, Ct);

        _publisher.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_log_names_only_the_code_id()
    {
        _code.Invalidate(Now.AddSeconds(-1));

        await Handler().HandleAsync(_requested, Ct);

        var line = _logger.Entries.ShouldHaveSingleItem().Message;
        line.ShouldContain(_code.Id.ToString());
        line.ShouldNotContain("60123456789");
    }

    private PublishPhoneVerificationRequestedDomainEventHandler Handler() =>
        new(_codes, _users, _publisher, _messageContext, new FakeTimeProvider(Now), _logger);
}
