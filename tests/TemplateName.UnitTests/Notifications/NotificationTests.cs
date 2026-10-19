using TemplateName.Modules.Notifications.Domain.Deliveries;
using TemplateName.Modules.Notifications.Domain.Notifications;

namespace TemplateName.UnitTests.Notifications;

public sealed class NotificationTests
{
    private const string Ciphertext = "CfDJ8-secret-ciphertext";

    private static readonly DateTimeOffset Now = new(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);

    private static Notification Create(
        DateTimeOffset? expiresAt = null,
        string? protectedData = null,
        string? correlationId = "4bf92f3577b34da6a3ce929d0e0e4736") =>
        Notification.Create(
            "auth.password_changed",
            NotificationPriority.Critical,
            Guid.NewGuid(),
            "ms",
            """{"display_name":"Alice"}""",
            protectedData,
            Guid.NewGuid(),
            correlationId,
            expiresAt,
            Now);

    [Fact]
    public void Create_stores_the_values_as_given_and_has_no_deliveries()
    {
        var userId = Guid.NewGuid();
        var sourceMessageId = Guid.NewGuid();
        var expiresAt = Now.AddMinutes(30);

        var notification = Notification.Create(
            "auth.password_reset_requested",
            NotificationPriority.Critical,
            userId,
            "zh-Hans",
            """{"expires_in_minutes":"30"}""",
            """{"action_url":"CfDJ8-ciphertext"}""",
            sourceMessageId,
            "trace-1",
            expiresAt,
            Now);

        notification.Id.ShouldNotBe(Guid.Empty);
        notification.TypeCode.ShouldBe("auth.password_reset_requested");
        notification.Priority.ShouldBe(NotificationPriority.Critical);
        notification.RecipientUserId.ShouldBe(userId);
        notification.Culture.ShouldBe("zh-Hans");
        notification.Data.ShouldBe("""{"expires_in_minutes":"30"}""");
        notification.ProtectedData.ShouldBe("""{"action_url":"CfDJ8-ciphertext"}""");
        notification.SourceMessageId.ShouldBe(sourceMessageId);
        notification.CorrelationId.ShouldBe("trace-1");
        notification.CreatedAt.ShouldBe(Now.UtcDateTime);
        notification.CreatedAt.Kind.ShouldBe(DateTimeKind.Utc);
        notification.ExpiresAt.ShouldBe(expiresAt.UtcDateTime);
        notification.Deliveries.ShouldBeEmpty();
    }

    [Fact]
    public void Create_keeps_absent_optional_values_null()
    {
        var notification = Create(expiresAt: null, protectedData: null, correlationId: null);

        notification.ExpiresAt.ShouldBeNull();
        notification.ProtectedData.ShouldBeNull();
        notification.CorrelationId.ShouldBeNull();
    }

    [Fact]
    public void Create_adds_one_delivery_per_channel_and_rejects_a_duplicate_channel()
    {
        var notification = Create(expiresAt: Now.AddMinutes(30));
        var firstAttemptAt = Now.AddHours(8);

        var email = notification.AddDelivery(NotificationChannel.Email, "alice@example.com", firstAttemptAt, Now);
        var inApp = notification.AddDelivery(NotificationChannel.InApp, null, Now, Now);

        notification.Deliveries.Count.ShouldBe(2);
        notification.Deliveries.ShouldContain(email);
        notification.Deliveries.ShouldContain(inApp);

        email.Id.ShouldNotBe(Guid.Empty);
        email.NotificationId.ShouldBe(notification.Id);
        email.Channel.ShouldBe(NotificationChannel.Email);
        email.Destination.ShouldBe("alice@example.com");
        email.Status.ShouldBe(DeliveryStatus.Pending);
        email.AttemptCount.ShouldBe(0);
        email.NextAttemptAt.ShouldBe(firstAttemptAt.UtcDateTime);
        email.NextAttemptAt!.Value.Kind.ShouldBe(DateTimeKind.Utc);
        email.LockedUntil.ShouldBeNull();
        email.LastError.ShouldBeNull();
        email.RenderedSubject.ShouldBeNull();
        email.CreatedAt.ShouldBe(Now.UtcDateTime);
        email.SentAt.ShouldBeNull();
        email.DeadLetteredAt.ShouldBeNull();
        email.ExpiresAt.ShouldBe(notification.ExpiresAt);

        inApp.Destination.ShouldBeNull();
        inApp.Id.ShouldNotBe(email.Id);

        Should.Throw<InvalidOperationException>(() => notification.AddDelivery(NotificationChannel.Email, "bob@example.com", Now, Now));
        notification.Deliveries.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-address")]
    [InlineData("alice@")]
    [InlineData("alice@example.com, mallory@example.com")]
    [InlineData("alice@example.com; mallory@example.com")]
    [InlineData("alice@example.com\r\nBcc: mallory@example.com")]
    [InlineData("alice@example.com\n")]
    [InlineData(" alice@example.com")]
    [InlineData("Alice <alice@example.com>")]
    [InlineData("\"Alice\" alice@example.com")]
    public void AddDelivery_refuses_an_email_delivery_without_a_single_valid_address(string? destination)
    {
        var notification = Create();

        var exception = Should.Throw<ArgumentException>(() => notification.AddDelivery(NotificationChannel.Email, destination, Now, Now));

        exception.ParamName.ShouldBe("destination");
        if (!string.IsNullOrEmpty(destination))
        {
            exception.Message.ShouldNotContain(destination);
        }

        notification.Deliveries.ShouldBeEmpty();
    }

    [Fact]
    public void AddDelivery_refuses_an_email_address_longer_than_the_column()
    {
        var notification = Create();
        var address = new string('a', 64) + "@" + new string('b', Delivery.MaxDestinationLength - 64 - 1) + ".com";
        address.Length.ShouldBe(Delivery.MaxDestinationLength + 4);

        Should.Throw<ArgumentException>(() => notification.AddDelivery(NotificationChannel.Email, address, Now, Now));
    }

    [Theory]
    [InlineData("alice@example.com")]
    [InlineData("Alice.Smith+tag@sub.example.co.uk")]
    [InlineData("alice@localhost.test")]
    public void AddDelivery_accepts_a_single_email_address(string destination)
        => Create().AddDelivery(NotificationChannel.Email, destination, Now, Now).Destination.ShouldBe(destination);

    [Fact]
    public void ToString_does_not_expose_the_protected_data()
    {
        var notification = Create(protectedData: $$"""{"action_url":"{{Ciphertext}}"}""");

        notification.ToString()!.ShouldNotContain(Ciphertext);
    }

    [Fact]
    public void Create_cuts_a_correlation_id_longer_than_the_column()
    {
        var notification = Create(correlationId: new string('a', 80));

        notification.CorrelationId!.Length.ShouldBe(Notification.MaxCorrelationIdLength);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Create_rejects_a_blank_type_code(string typeCode) =>
        Should.Throw<ArgumentException>(() => Notification.Create(
            typeCode, NotificationPriority.Normal, Guid.NewGuid(), "en", "{}", null, Guid.NewGuid(), null, null, Now));
}
