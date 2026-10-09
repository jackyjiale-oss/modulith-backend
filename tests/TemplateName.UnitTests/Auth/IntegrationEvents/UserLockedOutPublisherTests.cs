using NSubstitute;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.IntegrationEvents;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Auth.Domain.Users.Events;

namespace TemplateName.UnitTests.Auth.IntegrationEvents;

public sealed class UserLockedOutPublisherTests
{
    private static readonly Guid MessageId = new("0199c8a0-0000-7000-8000-000000000004");
    private static readonly DateTimeOffset MessageOccurredAt = new(2026, 10, 9, 8, 59, 30, TimeSpan.Zero);

    private readonly IOutboxMessageContext _messageContext = Substitute.For<IOutboxMessageContext>();
    private readonly RecordingIntegrationEventPublisher _publisher = new();

    public UserLockedOutPublisherTests()
    {
        _messageContext.MessageId.Returns(MessageId);
        _messageContext.OccurredAt.Returns(MessageOccurredAt);
    }

    [Fact]
    public async Task Publishes_the_user_and_lockout_end_with_the_outbox_message_id_and_time()
    {
        var userId = Guid.NewGuid();
        var lockoutEnd = new DateTimeOffset(2026, 10, 9, 9, 14, 30, TimeSpan.Zero);
        var handler = new PublishUserLockedOutDomainEventHandler(_publisher, _messageContext);

        await handler.HandleAsync(new UserLockedOutDomainEvent(userId, lockoutEnd), TestContext.Current.CancellationToken);

        _publisher.Published.ShouldHaveSingleItem().ShouldBe(new UserLockedOutIntegrationEvent(MessageId, MessageOccurredAt, userId, lockoutEnd));
    }
}
