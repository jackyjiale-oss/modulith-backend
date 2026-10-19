using NSubstitute;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.IntegrationEvents;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Users.Events;

namespace TemplateName.UnitTests.Auth.IntegrationEvents;

public sealed class MfaChangedPublisherTests
{
    private static readonly Guid MessageId = new("0199c8a0-0000-7000-8000-000000000011");
    private static readonly DateTimeOffset MessageOccurredAt = new(2026, 10, 17, 8, 59, 30, TimeSpan.Zero);

    private readonly IOutboxMessageContext _messageContext = Substitute.For<IOutboxMessageContext>();
    private readonly RecordingIntegrationEventPublisher _publisher = new();

    public MfaChangedPublisherTests()
    {
        _messageContext.MessageId.Returns(MessageId);
        _messageContext.OccurredAt.Returns(MessageOccurredAt);
    }

    [Theory]
    [InlineData(nameof(MfaChange.Enabled), MfaChangeKind.Enabled)]
    [InlineData(nameof(MfaChange.Disabled), MfaChangeKind.Disabled)]
    [InlineData(nameof(MfaChange.RecoveryCodesRegenerated), MfaChangeKind.RecoveryCodesRegenerated)]
    [InlineData(nameof(MfaChange.ResetByAdmin), MfaChangeKind.ResetByAdmin)]
    [InlineData(nameof(MfaChange.RecoveryCodeUsed), MfaChangeKind.RecoveryCodeUsed)]
    public async Task Mfa_changed_maps_every_change_kind(string changeName, MfaChangeKind expected)
    {
        var userId = Guid.NewGuid();
        var handler = new PublishMfaChangedDomainEventHandler(_publisher, _messageContext);

        await handler.HandleAsync(new MfaChangedDomainEvent(userId, Enum.Parse<MfaChange>(changeName)), TestContext.Current.CancellationToken);

        _publisher.Published.ShouldHaveSingleItem().ShouldBe(new MfaChangedIntegrationEvent(MessageId, MessageOccurredAt, userId, expected));
    }

    [Fact]
    public void Every_change_has_a_kind_of_the_same_name_and_value()
    {
        Enum.GetNames<MfaChange>().ShouldBe(Enum.GetNames<MfaChangeKind>());
        foreach (var change in Enum.GetValues<MfaChange>())
        {
            ((int)Enum.Parse<MfaChangeKind>(change.ToString())).ShouldBe((int)change);
        }
    }

    [Fact]
    public async Task Event_id_and_time_come_from_the_outbox_message()
    {
        var handler = new PublishMfaChangedDomainEventHandler(_publisher, _messageContext);

        await handler.HandleAsync(new MfaChangedDomainEvent(Guid.NewGuid(), MfaChange.Enabled), TestContext.Current.CancellationToken);

        var published = _publisher.Published.ShouldHaveSingleItem().ShouldBeOfType<MfaChangedIntegrationEvent>();
        published.Id.ShouldBe(MessageId);
        published.OccurredAt.ShouldBe(MessageOccurredAt);
    }

    [Fact]
    public async Task An_unmapped_change_throws_and_publishes_nothing()
    {
        var handler = new PublishMfaChangedDomainEventHandler(_publisher, _messageContext);

        await Should.ThrowAsync<InvalidOperationException>(
            () => handler.HandleAsync(new MfaChangedDomainEvent(Guid.NewGuid(), (MfaChange)99), TestContext.Current.CancellationToken));

        _publisher.Published.ShouldBeEmpty();
    }
}
