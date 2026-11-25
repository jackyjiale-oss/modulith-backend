using NSubstitute;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.IntegrationEvents;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Users.Events;

namespace TemplateName.UnitTests.Auth.IntegrationEvents;

public sealed class PhoneNumberChangedPublisherTests
{
    private static readonly Guid MessageId = new("0199c8a0-0000-7000-8000-000000000041");
    private static readonly DateTimeOffset MessageOccurredAt = new(2026, 11, 24, 8, 59, 30, TimeSpan.Zero);

    private readonly IOutboxMessageContext _messageContext = Substitute.For<IOutboxMessageContext>();
    private readonly RecordingIntegrationEventPublisher _publisher = new();

    public PhoneNumberChangedPublisherTests()
    {
        _messageContext.MessageId.Returns(MessageId);
        _messageContext.OccurredAt.Returns(MessageOccurredAt);
    }

    [Theory]
    [InlineData(nameof(PhoneNumberChange.Added), PhoneNumberChangeKind.Added)]
    [InlineData(nameof(PhoneNumberChange.Replaced), PhoneNumberChangeKind.Replaced)]
    [InlineData(nameof(PhoneNumberChange.Removed), PhoneNumberChangeKind.Removed)]
    public async Task Maps_kind_and_last_digits_only(string changeName, PhoneNumberChangeKind expected)
    {
        var userId = Guid.NewGuid();
        var handler = new PublishPhoneNumberChangedDomainEventHandler(_publisher, _messageContext);

        await handler.HandleAsync(
            new PhoneNumberChangedDomainEvent(userId, Enum.Parse<PhoneNumberChange>(changeName), "789", MessageOccurredAt),
            TestContext.Current.CancellationToken);

        _publisher.Published.ShouldHaveSingleItem()
            .ShouldBe(new PhoneNumberChangedIntegrationEvent(MessageId, MessageOccurredAt, userId, expected, "789"));
    }

    [Fact]
    public void The_contract_values_are_stable()
    {
        ((int)PhoneNumberChangeKind.Added).ShouldBe(1);
        ((int)PhoneNumberChangeKind.Replaced).ShouldBe(2);
        ((int)PhoneNumberChangeKind.Removed).ShouldBe(3);
    }
}
