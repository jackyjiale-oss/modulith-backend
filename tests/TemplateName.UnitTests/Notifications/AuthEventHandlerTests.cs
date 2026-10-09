using System.Text.Json;
using NSubstitute;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Notifications.Application.AuthEvents;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Domain.Notifications;

namespace TemplateName.UnitTests.Notifications;

/// <summary>
/// Each Auth event consumer through the real <c>NotificationScheduler</c> (over substitutes): the type it maps to, the variables it
/// passes (exactly the type's declared set, or the scheduler throws), the destination and the expiry, and its inbox consumer name.
/// </summary>
public sealed class AuthEventHandlerTests
{
    private const string Ciphertext = "CfDJ8-protected-link";
    private const string IssuedFor = "issued-for@example.com";
    private const string KualaLumpur = "Asia/Kuala_Lumpur";

    private static readonly DateTimeOffset Now = NotificationSchedulerHarness.Now;

    // 2026-03-01 08:05 UTC is 16:05 in Kuala Lumpur (UTC+8, no daylight saving).
    private static readonly DateTimeOffset OccurredAt = new(2026, 3, 1, 8, 5, 30, TimeSpan.Zero);

    private readonly NotificationSchedulerHarness _harness = new();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _eventId = Guid.NewGuid();

    public AuthEventHandlerTests()
    {
        _harness.AddContact(_userId, email: "current@example.com", displayName: "Alice", locale: "ms", timeZone: KualaLumpur);
        _harness.Decrypts(Ciphertext, "https://app.example.test/confirm?token=abc");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Maps_email_verification_to_its_type_and_variables()
    {
        var expiresAt = Now.AddMinutes(59).AddSeconds(30);
        var handler = new EmailVerificationRequestedIntegrationEventHandler(_harness.CreateScheduler(), _harness.Time);

        await handler.HandleAsync(new EmailVerificationRequestedIntegrationEvent(_eventId, OccurredAt, _userId, IssuedFor, Ciphertext, expiresAt), Ct);

        AssertLink(AuthNotificationTypes.EmailVerification, expiresAt, expectedMinutes: "60", handler.GetType());
    }

    [Theory]
    [InlineData(PasswordResetReason.SelfService, AuthNotificationTypes.PasswordReset)]
    [InlineData(PasswordResetReason.ForcedByAdmin, AuthNotificationTypes.PasswordResetRequired)]
    [InlineData(PasswordResetReason.CreatedByAdmin, AuthNotificationTypes.AccountCreated)]
    public async Task Maps_password_reset_to_its_type_and_variables(PasswordResetReason reason, string typeCode)
    {
        var expiresAt = Now.AddMinutes(30);
        var handler = new PasswordResetRequestedIntegrationEventHandler(_harness.CreateScheduler(), _harness.Time);

        await handler.HandleAsync(
            new PasswordResetRequestedIntegrationEvent(_eventId, OccurredAt, _userId, IssuedFor, Ciphertext, expiresAt, reason),
            Ct);

        AssertLink(typeCode, expiresAt, expectedMinutes: "30", handler.GetType());
    }

    [Fact]
    public async Task Unknown_password_reset_reason_throws()
    {
        var handler = new PasswordResetRequestedIntegrationEventHandler(_harness.CreateScheduler(), _harness.Time);
        var unknown = new PasswordResetRequestedIntegrationEvent(_eventId, OccurredAt, _userId, IssuedFor, Ciphertext, Now.AddMinutes(30), (PasswordResetReason)99);

        await Should.ThrowAsync<InvalidOperationException>(() => handler.HandleAsync(unknown, Ct));

        _harness.Added.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(1800, "30")]
    [InlineData(1741, "30")]
    [InlineData(1, "1")]
    [InlineData(0, "1")]
    [InlineData(-300, "1")]
    public async Task Expires_in_minutes_is_rounded_up_and_at_least_one(int secondsLeft, string expectedMinutes)
    {
        var handler = new EmailVerificationRequestedIntegrationEventHandler(_harness.CreateScheduler(), _harness.Time);

        await handler.HandleAsync(
            new EmailVerificationRequestedIntegrationEvent(_eventId, OccurredAt, _userId, IssuedFor, Ciphertext, Now.AddSeconds(secondsLeft)),
            Ct);

        Variables(_harness.Added.ShouldHaveSingleItem().Data)["expires_in_minutes"].ShouldBe(expectedMinutes);
    }

    [Fact]
    public async Task Maps_registration_attempted_to_its_type_and_variables()
    {
        var handler = new RegistrationAttemptedIntegrationEventHandler(_harness.CreateScheduler());

        await handler.HandleAsync(new RegistrationAttemptedIntegrationEvent(_eventId, OccurredAt, _userId), Ct);

        var notification = AssertNotice(AuthNotificationTypes.RegistrationAttempted, handler.GetType());
        Variables(notification.Data).ShouldBe(new Dictionary<string, string> { ["display_name"] = "Alice" });
        notification.Deliveries.ShouldHaveSingleItem().Destination.ShouldBe("current@example.com");
    }

    [Fact]
    public async Task Maps_password_changed_to_its_type_and_variables()
    {
        var handler = new PasswordChangedIntegrationEventHandler(_harness.CreateScheduler());

        await handler.HandleAsync(new PasswordChangedIntegrationEvent(_eventId, OccurredAt, _userId), Ct);

        var notification = AssertNotice(AuthNotificationTypes.PasswordChanged, handler.GetType());
        Variables(notification.Data).ShouldBe(new Dictionary<string, string>
        {
            ["display_name"] = "Alice",
            ["occurred_at"] = "2026-03-01 16:05",
            ["time_zone"] = KualaLumpur,
        });
        notification.Deliveries.Select(delivery => (delivery.Channel, delivery.Destination))
            .ShouldBe([(NotificationChannel.Email, "current@example.com"), (NotificationChannel.InApp, (string?)null)]);
    }

    [Fact]
    public async Task Maps_user_locked_out_to_its_type_and_variables()
    {
        var handler = new UserLockedOutIntegrationEventHandler(_harness.CreateScheduler());
        var lockoutEnd = new DateTimeOffset(2026, 3, 1, 16, 20, 0, TimeSpan.Zero);

        await handler.HandleAsync(new UserLockedOutIntegrationEvent(_eventId, OccurredAt, _userId, lockoutEnd), Ct);

        var notification = AssertNotice(AuthNotificationTypes.AccountLocked, handler.GetType());
        Variables(notification.Data).ShouldBe(new Dictionary<string, string>
        {
            ["display_name"] = "Alice",
            ["locked_until"] = "2026-03-02 00:20",
            ["time_zone"] = KualaLumpur,
        });
        notification.Deliveries.ShouldHaveSingleItem().Channel.ShouldBe(NotificationChannel.Email);
    }

    [Fact]
    public async Task Maps_refresh_token_reuse_to_its_type_and_variables()
    {
        var handler = new RefreshTokenReuseDetectedIntegrationEventHandler(_harness.CreateScheduler());

        await handler.HandleAsync(new RefreshTokenReuseDetectedIntegrationEvent(_eventId, OccurredAt, _userId, Guid.NewGuid()), Ct);

        var notification = AssertNotice(AuthNotificationTypes.TokenReuseDetected, handler.GetType());
        Variables(notification.Data).ShouldBe(new Dictionary<string, string>
        {
            ["display_name"] = "Alice",
            ["occurred_at"] = "2026-03-01 16:05",
            ["time_zone"] = KualaLumpur,
        });
        notification.Deliveries.Select(delivery => delivery.Channel).ShouldBe([NotificationChannel.Email, NotificationChannel.InApp]);
    }

    [Theory]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("")]
    public async Task Local_times_of_an_unusable_time_zone_are_in_utc(string timeZone)
    {
        _harness.AddContact(_userId, timeZone: timeZone);
        var handler = new PasswordChangedIntegrationEventHandler(_harness.CreateScheduler());

        await handler.HandleAsync(new PasswordChangedIntegrationEvent(_eventId, OccurredAt, _userId), Ct);

        var variables = Variables(_harness.Added.ShouldHaveSingleItem().Data);
        variables["occurred_at"].ShouldBe("2026-03-01 08:05");
        variables["time_zone"].ShouldBe("UTC");
    }

    private static Dictionary<string, string> Variables(string json) => JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;

    private void AssertLink(string typeCode, DateTimeOffset expiresAt, string expectedMinutes, Type handlerType)
    {
        var notification = _harness.Added.ShouldHaveSingleItem();
        notification.TypeCode.ShouldBe(typeCode);
        notification.RecipientUserId.ShouldBe(_userId);
        notification.SourceMessageId.ShouldBe(_eventId);
        notification.Culture.ShouldBe("ms");
        notification.ExpiresAt.ShouldBe(expiresAt.UtcDateTime);
        Variables(notification.Data).ShouldBe(new Dictionary<string, string> { ["display_name"] = "Alice", ["expires_in_minutes"] = expectedMinutes });
        Variables(notification.ProtectedData!).ShouldBe(new Dictionary<string, string> { ["action_url"] = Ciphertext });

        // The link goes to the address it was issued for, not the user's current one.
        var delivery = notification.Deliveries.ShouldHaveSingleItem();
        delivery.Channel.ShouldBe(NotificationChannel.Email);
        delivery.Destination.ShouldBe(IssuedFor);
        delivery.ExpiresAt.ShouldBe(expiresAt.UtcDateTime);
        _harness.Inbox.Received(1).Record(_eventId, handlerType.FullName!);
    }

    private Notification AssertNotice(string typeCode, Type handlerType)
    {
        var notification = _harness.Added.ShouldHaveSingleItem();
        notification.TypeCode.ShouldBe(typeCode);
        notification.RecipientUserId.ShouldBe(_userId);
        notification.SourceMessageId.ShouldBe(_eventId);
        notification.ExpiresAt.ShouldBeNull();
        notification.ProtectedData.ShouldBeNull();
        _harness.Inbox.Received(1).Record(_eventId, handlerType.FullName!);
        return notification;
    }
}
