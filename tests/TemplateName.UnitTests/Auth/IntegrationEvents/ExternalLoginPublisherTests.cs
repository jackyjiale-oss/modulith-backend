using NSubstitute;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.IntegrationEvents;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Auth.Domain.Users.Events;

namespace TemplateName.UnitTests.Auth.IntegrationEvents;

public sealed class ExternalLoginPublisherTests
{
    private static readonly Guid MessageId = new("0199c8a0-0000-7000-8000-000000000021");
    private static readonly DateTimeOffset MessageOccurredAt = new(2026, 10, 30, 7, 45, 0, TimeSpan.Zero);

    private readonly IOutboxMessageContext _messageContext = Substitute.For<IOutboxMessageContext>();
    private readonly IExternalProviderCatalog _catalog = Substitute.For<IExternalProviderCatalog>();
    private readonly RecordingIntegrationEventPublisher _publisher = new();

    public ExternalLoginPublisherTests()
    {
        _messageContext.MessageId.Returns(MessageId);
        _messageContext.OccurredAt.Returns(MessageOccurredAt);
        _catalog.DisplayNameOf("google").Returns("Google");
        _catalog.DisplayNameOf("retired-provider").Returns("retired-provider");
    }

    [Fact]
    public async Task Linked_publishes_the_provider_and_its_display_name()
    {
        var userId = Guid.NewGuid();
        var handler = new PublishExternalLoginLinkedDomainEventHandler(_publisher, _messageContext, _catalog);

        await handler.HandleAsync(new ExternalLoginLinkedDomainEvent(userId, "google"), TestContext.Current.CancellationToken);

        _publisher.Published.ShouldHaveSingleItem()
            .ShouldBe(new ExternalLoginLinkedIntegrationEvent(MessageId, MessageOccurredAt, userId, "google", "Google"));
    }

    [Fact]
    public async Task Unlinked_publishes_the_provider_and_its_display_name()
    {
        var userId = Guid.NewGuid();
        var handler = new PublishExternalLoginUnlinkedDomainEventHandler(_publisher, _messageContext, _catalog);

        await handler.HandleAsync(new ExternalLoginUnlinkedDomainEvent(userId, "google"), TestContext.Current.CancellationToken);

        _publisher.Published.ShouldHaveSingleItem()
            .ShouldBe(new ExternalLoginUnlinkedIntegrationEvent(MessageId, MessageOccurredAt, userId, "google", "Google"));
    }

    [Fact]
    public async Task Linked_of_a_provider_that_is_no_longer_configured_publishes_the_key_as_its_name()
    {
        var userId = Guid.NewGuid();
        var handler = new PublishExternalLoginLinkedDomainEventHandler(_publisher, _messageContext, _catalog);

        await handler.HandleAsync(new ExternalLoginLinkedDomainEvent(userId, "retired-provider"), TestContext.Current.CancellationToken);

        _publisher.Published.ShouldHaveSingleItem()
            .ShouldBe(new ExternalLoginLinkedIntegrationEvent(MessageId, MessageOccurredAt, userId, "retired-provider", "retired-provider"));
    }

    [Fact]
    public async Task Unlinked_of_a_provider_that_is_no_longer_configured_publishes_the_key_as_its_name()
    {
        var userId = Guid.NewGuid();
        var handler = new PublishExternalLoginUnlinkedDomainEventHandler(_publisher, _messageContext, _catalog);

        await handler.HandleAsync(new ExternalLoginUnlinkedDomainEvent(userId, "retired-provider"), TestContext.Current.CancellationToken);

        _publisher.Published.ShouldHaveSingleItem()
            .ShouldBe(new ExternalLoginUnlinkedIntegrationEvent(MessageId, MessageOccurredAt, userId, "retired-provider", "retired-provider"));
    }

    [Fact]
    public async Task Event_id_and_time_come_from_the_outbox_message()
    {
        var linked = new PublishExternalLoginLinkedDomainEventHandler(_publisher, _messageContext, _catalog);
        var unlinked = new PublishExternalLoginUnlinkedDomainEventHandler(_publisher, _messageContext, _catalog);

        await linked.HandleAsync(new ExternalLoginLinkedDomainEvent(Guid.NewGuid(), "google"), TestContext.Current.CancellationToken);
        await unlinked.HandleAsync(new ExternalLoginUnlinkedDomainEvent(Guid.NewGuid(), "google"), TestContext.Current.CancellationToken);

        _publisher.Published.Count.ShouldBe(2);
        _publisher.Published.OfType<ExternalLoginLinkedIntegrationEvent>().ShouldHaveSingleItem().Id.ShouldBe(MessageId);
        var unlinkedEvent = _publisher.Published.OfType<ExternalLoginUnlinkedIntegrationEvent>().ShouldHaveSingleItem();
        unlinkedEvent.Id.ShouldBe(MessageId);
        unlinkedEvent.OccurredAt.ShouldBe(MessageOccurredAt);
    }
}
