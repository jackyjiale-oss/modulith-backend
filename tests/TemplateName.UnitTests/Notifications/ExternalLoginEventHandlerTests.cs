using System.Text.Json;
using NSubstitute;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Notifications.Application.AuthEvents;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Domain.Notifications;

namespace TemplateName.UnitTests.Notifications;

/// <summary>
/// The consumers of the social-login link and unlink events through the real <c>NotificationScheduler</c> (over substitutes): the type
/// each maps to, the variables it passes (the provider's display name and the local time), the channels and its inbox consumer name.
/// </summary>
public sealed class ExternalLoginEventHandlerTests
{
    private const string KualaLumpur = "Asia/Kuala_Lumpur";

    // 2026-03-01 08:05 UTC is 16:05 in Kuala Lumpur (UTC+8, no daylight saving).
    private static readonly DateTimeOffset OccurredAt = new(2026, 3, 1, 8, 5, 30, TimeSpan.Zero);

    private readonly NotificationSchedulerHarness _harness = new();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _eventId = Guid.NewGuid();

    public ExternalLoginEventHandlerTests()
    {
        _harness.AddContact(_userId, email: "current@example.com", displayName: "Alice", locale: "ms", timeZone: KualaLumpur);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Maps_external_login_linked_to_its_type_and_variables()
    {
        var handler = new ExternalLoginLinkedIntegrationEventHandler(_harness.CreateScheduler());

        await handler.HandleAsync(new ExternalLoginLinkedIntegrationEvent(_eventId, OccurredAt, _userId, "google", "Google"), Ct);

        AssertNotice(AuthNotificationTypes.ExternalLoginLinked, handler.GetType(), "Google");
    }

    [Fact]
    public async Task Maps_external_login_unlinked_to_its_type_and_variables()
    {
        var handler = new ExternalLoginUnlinkedIntegrationEventHandler(_harness.CreateScheduler());

        await handler.HandleAsync(new ExternalLoginUnlinkedIntegrationEvent(_eventId, OccurredAt, _userId, "google", "Google"), Ct);

        AssertNotice(AuthNotificationTypes.ExternalLoginUnlinked, handler.GetType(), "Google");
    }

    [Fact]
    public async Task Linked_names_the_provider_by_its_display_name_not_its_key()
    {
        var handler = new ExternalLoginLinkedIntegrationEventHandler(_harness.CreateScheduler());

        await handler.HandleAsync(new ExternalLoginLinkedIntegrationEvent(_eventId, OccurredAt, _userId, "corp-sso", "Acme Single Sign-On"), Ct);

        Variables(_harness.Added.ShouldHaveSingleItem().Data)["provider"].ShouldBe("Acme Single Sign-On");
    }

    [Fact]
    public async Task Unlinked_of_a_provider_that_is_no_longer_configured_shows_the_key()
    {
        var handler = new ExternalLoginUnlinkedIntegrationEventHandler(_harness.CreateScheduler());

        await handler.HandleAsync(new ExternalLoginUnlinkedIntegrationEvent(_eventId, OccurredAt, _userId, "retired", "retired"), Ct);

        Variables(_harness.Added.ShouldHaveSingleItem().Data)["provider"].ShouldBe("retired");
    }

    [Fact]
    public async Task Local_time_of_an_unusable_time_zone_is_in_utc()
    {
        _harness.AddContact(_userId, timeZone: "Mars/Olympus_Mons");
        var handler = new ExternalLoginLinkedIntegrationEventHandler(_harness.CreateScheduler());

        await handler.HandleAsync(new ExternalLoginLinkedIntegrationEvent(_eventId, OccurredAt, _userId, "google", "Google"), Ct);

        var variables = Variables(_harness.Added.ShouldHaveSingleItem().Data);
        variables["occurred_at"].ShouldBe("2026-03-01 08:05");
        variables["time_zone"].ShouldBe("UTC");
    }

    private static Dictionary<string, string> Variables(string json) => JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;

    private void AssertNotice(string typeCode, Type handlerType, string expectedProvider)
    {
        var notification = _harness.Added.ShouldHaveSingleItem();
        notification.TypeCode.ShouldBe(typeCode);
        notification.RecipientUserId.ShouldBe(_userId);
        notification.SourceMessageId.ShouldBe(_eventId);
        notification.Culture.ShouldBe("ms");
        notification.ExpiresAt.ShouldBeNull();
        notification.ProtectedData.ShouldBeNull();
        Variables(notification.Data).ShouldBe(new Dictionary<string, string>
        {
            ["display_name"] = "Alice",
            ["occurred_at"] = "2026-03-01 16:05",
            ["provider"] = expectedProvider,
            ["time_zone"] = KualaLumpur,
        });

        // Email to the user's current address, plus the optional in-app copy, as for a password change.
        notification.Deliveries.Select(delivery => (delivery.Channel, delivery.Destination))
            .ShouldBe([(NotificationChannel.Email, "current@example.com"), (NotificationChannel.InApp, (string?)null)]);
        _harness.Inbox.Received(1).Record(_eventId, handlerType.FullName!);
    }
}
