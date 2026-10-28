using NSubstitute;
using TemplateName.Application.Common.Auditing;
using TemplateName.Application.Common.Messaging;
using TemplateName.Infrastructure.Common.Auditing;
using TemplateName.SharedKernel.Auditing;
using TemplateName.UnitTests.Auth.IntegrationEvents;

namespace TemplateName.UnitTests.Infrastructure.Auditing;

public sealed class EntityChangesRecordedDomainEventHandlerTests
{
    private static readonly Guid MessageId = new("0199c8a0-0000-7000-8000-000000000042");
    private static readonly DateTimeOffset MessageOccurredAt = new(2026, 10, 28, 9, 15, 0, TimeSpan.Zero);

    private readonly IOutboxMessageContext _messageContext = Substitute.For<IOutboxMessageContext>();
    private readonly RecordingIntegrationEventPublisher _publisher = new();

    public EntityChangesRecordedDomainEventHandlerTests()
    {
        _messageContext.MessageId.Returns(MessageId);
        _messageContext.OccurredAt.Returns(MessageOccurredAt);
    }

    [Fact]
    public async Task Publishes_the_change_set_with_the_outbox_message_id_and_time()
    {
        IReadOnlyList<EntityChange> changes =
        [
            new("LeaveRequest", "0a7c1b52-3d4e-4f60-8a91-b2c3d4e5f607", EntityChangeAction.Updated, """{"Status":{"old":"Pending","new":"Approved"}}"""),
        ];
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var domainEvent = new EntityChangesRecordedDomainEvent(userId, sessionId, "4bf92f3577b34da6a3ce929d0e0e4736", "sample", changes);
        var handler = new EntityChangesRecordedDomainEventHandler(_messageContext, _publisher);

        await handler.HandleAsync(domainEvent, TestContext.Current.CancellationToken);

        var published = _publisher.Published.ShouldHaveSingleItem().ShouldBeOfType<EntityChangesRecordedIntegrationEvent>();
        published.Id.ShouldBe(MessageId);
        published.OccurredAt.ShouldBe(MessageOccurredAt);
        published.UserId.ShouldBe(userId);
        published.SessionId.ShouldBe(sessionId);
        published.TraceId.ShouldBe("4bf92f3577b34da6a3ce929d0e0e4736");
        published.Module.ShouldBe("sample");
        published.Changes.ShouldBeSameAs(changes);
    }
}
