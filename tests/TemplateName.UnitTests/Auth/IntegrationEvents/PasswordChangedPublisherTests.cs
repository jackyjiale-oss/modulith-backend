using NSubstitute;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.IntegrationEvents;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Auth.Domain.Users.Events;

namespace TemplateName.UnitTests.Auth.IntegrationEvents;

public sealed class PasswordChangedPublisherTests
{
    private static readonly Guid MessageId = new("0199c8a0-0000-7000-8000-000000000003");
    private static readonly DateTimeOffset MessageOccurredAt = new(2026, 10, 9, 8, 59, 30, TimeSpan.Zero);

    private readonly IOutboxMessageContext _messageContext = Substitute.For<IOutboxMessageContext>();
    private readonly RecordingIntegrationEventPublisher _publisher = new();

    public PasswordChangedPublisherTests()
    {
        _messageContext.MessageId.Returns(MessageId);
        _messageContext.OccurredAt.Returns(MessageOccurredAt);
    }

    [Fact]
    public async Task Publishes_the_user_with_the_outbox_message_id_and_time()
    {
        var userId = Guid.NewGuid();
        var handler = new PublishPasswordChangedDomainEventHandler(_publisher, _messageContext);

        await handler.HandleAsync(new PasswordChangedDomainEvent(userId, "alice@example.com", "en"), TestContext.Current.CancellationToken);

        _publisher.Published.ShouldHaveSingleItem().ShouldBe(new PasswordChangedIntegrationEvent(MessageId, MessageOccurredAt, userId));
    }
}
