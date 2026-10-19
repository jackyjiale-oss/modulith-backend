using NSubstitute;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.IntegrationEvents;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Auth.Domain.Sessions.Events;

namespace TemplateName.UnitTests.Auth.IntegrationEvents;

public sealed class RefreshTokenReuseDetectedPublisherTests
{
    private static readonly Guid MessageId = new("0199c8a0-0000-7000-8000-000000000005");
    private static readonly DateTimeOffset MessageOccurredAt = new(2026, 10, 9, 8, 59, 30, TimeSpan.Zero);

    private readonly IOutboxMessageContext _messageContext = Substitute.For<IOutboxMessageContext>();
    private readonly RecordingIntegrationEventPublisher _publisher = new();

    public RefreshTokenReuseDetectedPublisherTests()
    {
        _messageContext.MessageId.Returns(MessageId);
        _messageContext.OccurredAt.Returns(MessageOccurredAt);
    }

    [Fact]
    public async Task Publishes_the_user_and_session_with_the_outbox_message_id_and_time()
    {
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var handler = new PublishRefreshTokenReuseDetectedDomainEventHandler(_publisher, _messageContext);

        await handler.HandleAsync(new RefreshTokenReuseDetectedDomainEvent(userId, sessionId), TestContext.Current.CancellationToken);

        _publisher.Published.ShouldHaveSingleItem()
            .ShouldBe(new RefreshTokenReuseDetectedIntegrationEvent(MessageId, MessageOccurredAt, userId, sessionId));
    }
}
