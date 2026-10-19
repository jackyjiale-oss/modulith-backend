using NSubstitute;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.IntegrationEvents;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Auth.Domain.Sessions.Events;

namespace TemplateName.UnitTests.Auth.IntegrationEvents;

public sealed class NewDeviceSignedInPublisherTests
{
    private static readonly Guid MessageId = new("0199c8a0-0000-7000-8000-000000000012");
    private static readonly DateTimeOffset MessageOccurredAt = new(2026, 10, 17, 9, 15, 0, TimeSpan.Zero);

    private readonly IOutboxMessageContext _messageContext = Substitute.For<IOutboxMessageContext>();
    private readonly RecordingIntegrationEventPublisher _publisher = new();

    public NewDeviceSignedInPublisherTests()
    {
        _messageContext.MessageId.Returns(MessageId);
        _messageContext.OccurredAt.Returns(MessageOccurredAt);
    }

    [Fact]
    public async Task New_device_publishes_session_and_address()
    {
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var handler = new PublishNewDeviceSignedInDomainEventHandler(_publisher, _messageContext);

        await handler.HandleAsync(new NewDeviceSignedInDomainEvent(userId, sessionId, "203.0.113.7"), TestContext.Current.CancellationToken);

        _publisher.Published.ShouldHaveSingleItem()
            .ShouldBe(new NewDeviceSignedInIntegrationEvent(MessageId, MessageOccurredAt, userId, sessionId, "203.0.113.7"));
    }

    [Fact]
    public async Task A_missing_address_is_published_as_null()
    {
        var handler = new PublishNewDeviceSignedInDomainEventHandler(_publisher, _messageContext);

        await handler.HandleAsync(new NewDeviceSignedInDomainEvent(Guid.NewGuid(), Guid.NewGuid(), null), TestContext.Current.CancellationToken);

        _publisher.Published.ShouldHaveSingleItem().ShouldBeOfType<NewDeviceSignedInIntegrationEvent>().IpAddress.ShouldBeNull();
    }

    [Fact]
    public async Task Event_id_and_time_come_from_the_outbox_message()
    {
        var handler = new PublishNewDeviceSignedInDomainEventHandler(_publisher, _messageContext);

        await handler.HandleAsync(new NewDeviceSignedInDomainEvent(Guid.NewGuid(), Guid.NewGuid(), "203.0.113.7"), TestContext.Current.CancellationToken);

        var published = _publisher.Published.ShouldHaveSingleItem().ShouldBeOfType<NewDeviceSignedInIntegrationEvent>();
        published.Id.ShouldBe(MessageId);
        published.OccurredAt.ShouldBe(MessageOccurredAt);
    }
}
