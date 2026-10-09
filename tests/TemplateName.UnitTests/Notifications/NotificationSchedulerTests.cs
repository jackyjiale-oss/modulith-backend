using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Application.Scheduling;
using TemplateName.Modules.Notifications.Domain.Deliveries;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.Modules.Notifications.Domain.Preferences;

namespace TemplateName.UnitTests.Notifications;

public sealed class NotificationSchedulerTests
{
    private const string Consumer = "TemplateName.Tests.Consumer";
    private const string Ciphertext = "CfDJ8-protected-link";
    private const string PlaintextLink = "https://app.example.test/reset?token=secret-token";
    private const string KualaLumpur = "Asia/Kuala_Lumpur";

    private static readonly DateTimeOffset Now = NotificationSchedulerHarness.Now;
    private static readonly TimeOnly QuietStart = new(22, 0);
    private static readonly TimeOnly QuietEnd = new(7, 0);

    private readonly NotificationSchedulerHarness _harness = new();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _messageId = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Scheduled_notification_snapshots_the_type_culture_variables_and_expiry()
    {
        _harness.AddContact(_userId, locale: "zh-CN", displayName: "Alice");
        var expiresAt = Now.AddMinutes(30);

        var outcome = await ScheduleAsync(PasswordChanged() with { ExpiresAt = expiresAt }, OccurredAt);

        outcome.ShouldBe(ScheduleOutcome.Scheduled);
        var notification = _harness.Added.ShouldHaveSingleItem();
        notification.TypeCode.ShouldBe(AuthNotificationTypes.PasswordChanged);
        notification.Priority.ShouldBe(NotificationPriority.Critical);
        notification.RecipientUserId.ShouldBe(_userId);
        notification.SourceMessageId.ShouldBe(_messageId);
        notification.Culture.ShouldBe("zh-Hans");
        notification.ExpiresAt.ShouldBe(expiresAt.UtcDateTime);
        Variables(notification.Data).ShouldBe(new Dictionary<string, string>
        {
            ["display_name"] = "Alice",
            ["occurred_at"] = "2026-03-01 15:00",
            ["time_zone"] = "UTC",
        });
        notification.ProtectedData.ShouldBeNull();
        notification.Deliveries.Select(delivery => delivery.Channel).ShouldBe([NotificationChannel.Email, NotificationChannel.InApp]);
        _harness.Inbox.Received(1).Record(_messageId, Consumer);
        await _harness.UnitOfWork.Received(1).SaveChangesUnlessInboxDuplicateAsync(Ct);
        await _harness.UnitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        _harness.Metrics.Received(1).RecordCreated(AuthNotificationTypes.PasswordChanged);
    }

    [Fact]
    public async Task Disabled_optional_channel_is_skipped_but_mandatory_email_kept()
    {
        _harness.AddContact(_userId);
        _harness.SetPreferences(
            _userId,
            UserPreference.Create(_userId, AuthNotificationTypes.PasswordChanged, NotificationChannel.InApp, isEnabled: false, Now),
            UserPreference.Create(_userId, AuthNotificationTypes.PasswordChanged, NotificationChannel.Email, isEnabled: false, Now),
            UserPreference.Create(_userId, AuthNotificationTypes.TokenReuseDetected, NotificationChannel.InApp, isEnabled: true, Now));

        var outcome = await ScheduleAsync(PasswordChanged(), OccurredAt);

        outcome.ShouldBe(ScheduleOutcome.Scheduled);
        var delivery = _harness.Added.ShouldHaveSingleItem().Deliveries.ShouldHaveSingleItem();
        delivery.Channel.ShouldBe(NotificationChannel.Email);
        delivery.Destination.ShouldBe("alice@example.com");
    }

    [Fact]
    public async Task Normal_email_inside_quiet_hours_is_deferred_to_the_window_end()
    {
        _harness.AddContact(_userId, timeZone: KualaLumpur);
        _harness.SetQuietHours(_userId, QuietStart, QuietEnd);

        var outcome = await ScheduleAsync(Welcome());

        outcome.ShouldBe(ScheduleOutcome.Scheduled);
        var email = Delivery(NotificationChannel.Email);
        email.NextAttemptAt.ShouldBe(new DateTime(2026, 3, 1, 23, 0, 0, DateTimeKind.Utc));
        email.Destination.ShouldBe("alice@example.com");
    }

    [Fact]
    public async Task Security_notice_ignores_quiet_hours()
    {
        _harness.AddContact(_userId, timeZone: KualaLumpur);
        _harness.SetQuietHours(_userId, QuietStart, QuietEnd);

        await ScheduleAsync(PasswordChanged(), OccurredAt);

        Delivery(NotificationChannel.Email).NextAttemptAt.ShouldBe(Now.UtcDateTime);
        Delivery(NotificationChannel.InApp).NextAttemptAt.ShouldBe(Now.UtcDateTime);
    }

    [Fact]
    public async Task In_app_is_never_deferred()
    {
        _harness.AddContact(_userId, timeZone: KualaLumpur);
        _harness.SetQuietHours(_userId, QuietStart, QuietEnd);

        await ScheduleAsync(Welcome());

        var inApp = Delivery(NotificationChannel.InApp);
        inApp.NextAttemptAt.ShouldBe(Now.UtcDateTime);
        inApp.Destination.ShouldBeNull();
    }

    [Fact]
    public async Task Email_outside_quiet_hours_is_due_now()
    {
        _harness.AddContact(_userId, timeZone: "UTC");
        _harness.SetQuietHours(_userId, QuietStart, QuietEnd);

        await ScheduleAsync(Welcome());

        Delivery(NotificationChannel.Email).NextAttemptAt.ShouldBe(Now.UtcDateTime);
    }

    [Theory]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Invalid_time_zone_is_treated_as_utc(string timeZone)
    {
        // 23:30 UTC is inside 22:00 to 07:00 in UTC (it would be 07:30 in Kuala Lumpur, outside the window).
        _harness.Time.SetUtcNow(new DateTimeOffset(2026, 3, 1, 23, 30, 0, TimeSpan.Zero));
        _harness.AddContact(_userId, timeZone: timeZone);
        _harness.SetQuietHours(_userId, QuietStart, QuietEnd);

        var outcome = await ScheduleAsync(Welcome());

        outcome.ShouldBe(ScheduleOutcome.Scheduled);
        Delivery(NotificationChannel.Email).NextAttemptAt.ShouldBe(new DateTime(2026, 3, 2, 7, 0, 0, DateTimeKind.Utc));
        var warning = _harness.Logger.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldContain(_userId.ToString());
        UserIdOnly(warning.Properties);
        if (timeZone.Trim().Length > 0)
        {
            warning.Message.ShouldNotContain(timeZone);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("!!")]
    [InlineData("fr-FR")]
    [InlineData("zh-Hant")]
    public async Task Unsupported_locale_is_snapshotted_as_en(string locale)
    {
        _harness.AddContact(_userId, locale: locale);

        await ScheduleAsync(PasswordChanged(), OccurredAt);

        _harness.Added.ShouldHaveSingleItem().Culture.ShouldBe("en");
    }

    [Fact]
    public async Task Missing_recipient_records_the_inbox_and_creates_nothing()
    {
        var outcome = await ScheduleAsync(PasswordChanged(), OccurredAt);

        outcome.ShouldBe(ScheduleOutcome.RecipientNotFound);
        _harness.Added.ShouldBeEmpty();
        _harness.Inbox.Received(1).Record(_messageId, Consumer);
        await _harness.UnitOfWork.Received(1).SaveChangesUnlessInboxDuplicateAsync(Ct);
        _harness.Metrics.DidNotReceiveWithAnyArgs().RecordCreated(default!);
        var information = _harness.Logger.Entries.ShouldHaveSingleItem();
        information.Level.ShouldBe(LogLevel.Information);
        information.Message.ShouldContain(_userId.ToString());
        UserIdOnly(information.Properties);
    }

    [Fact]
    public async Task Protected_values_are_stored_unchanged()
    {
        _harness.AddContact(_userId);
        _harness.Decrypts(Ciphertext, PlaintextLink);

        var outcome = await ScheduleAsync(PasswordReset(), contactVariables: null);

        outcome.ShouldBe(ScheduleOutcome.Scheduled);
        var notification = _harness.Added.ShouldHaveSingleItem();
        Variables(notification.ProtectedData!).ShouldBe(new Dictionary<string, string> { ["action_url"] = Ciphertext });
        Variables(notification.Data).ShouldBe(new Dictionary<string, string> { ["display_name"] = "Alice", ["expires_in_minutes"] = "30" });
        notification.Data.ShouldNotContain("secret-token");
        notification.ProtectedData!.ShouldNotContain("secret-token");
        _harness.SecretProtector.DidNotReceiveWithAnyArgs().Protect(default!);
        _harness.Logger.Entries.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("javascript:alert(document.cookie)")]
    [InlineData("JavaScript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("ftp://app.example.test/reset")]
    [InlineData("file:///etc/passwd")]
    [InlineData("/reset?token=secret-token")]
    [InlineData("app.example.test/reset?token=secret-token")]
    [InlineData("")]
    public async Task Link_with_a_non_http_scheme_is_refused_and_creates_nothing(string plaintext)
    {
        _harness.AddContact(_userId);
        _harness.Decrypts(Ciphertext, plaintext);

        var outcome = await ScheduleAsync(PasswordReset(), contactVariables: null);

        await AssertRefusedAsync(outcome, plaintext);
    }

    [Fact]
    public async Task Undecryptable_link_is_refused_and_creates_nothing()
    {
        _harness.AddContact(_userId);
        _harness.SecretProtector.Unprotect(Ciphertext).Throws(new CryptographicException("The payload was invalid."));

        var outcome = await ScheduleAsync(PasswordReset(), contactVariables: null);

        await AssertRefusedAsync(outcome, plaintext: null);
    }

    [Fact]
    public async Task Already_processed_message_does_nothing()
    {
        _harness.AddContact(_userId);
        _harness.Inbox.HasProcessedAsync(_messageId, Consumer, Arg.Any<CancellationToken>()).Returns(true);

        var outcome = await ScheduleAsync(PasswordReset(), contactVariables: null);

        outcome.ShouldBe(ScheduleOutcome.AlreadyProcessed);
        _harness.Added.ShouldBeEmpty();
        await _harness.Contacts.DidNotReceiveWithAnyArgs().FindAsync(default, Ct);
        _harness.SecretProtector.DidNotReceiveWithAnyArgs().Unprotect(default!);
        _harness.Inbox.DidNotReceiveWithAnyArgs().Record(default, default!);
        await _harness.UnitOfWork.DidNotReceiveWithAnyArgs().SaveChangesUnlessInboxDuplicateAsync(Ct);
        _harness.Metrics.DidNotReceiveWithAnyArgs().RecordCreated(default!);
    }

    [Fact]
    public async Task Lost_inbox_race_returns_already_processed_without_counting()
    {
        _harness.AddContact(_userId);
        _harness.UnitOfWork.SaveChangesUnlessInboxDuplicateAsync(Arg.Any<CancellationToken>()).Returns(false);

        var outcome = await ScheduleAsync(PasswordChanged(), OccurredAt);

        outcome.ShouldBe(ScheduleOutcome.AlreadyProcessed);
        _harness.Metrics.DidNotReceiveWithAnyArgs().RecordCreated(default!);
    }

    [Fact]
    public async Task Email_goes_to_the_address_the_link_was_issued_for()
    {
        _harness.AddContact(_userId, email: "current@example.com");
        _harness.Decrypts(Ciphertext, PlaintextLink);

        await ScheduleAsync(PasswordReset() with { EmailOverride = "issued-for@example.com" }, contactVariables: null);

        Delivery(NotificationChannel.Email).Destination.ShouldBe("issued-for@example.com");
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("alice@example.com, mallory@example.com")]
    [InlineData("alice@example.com; mallory@example.com")]
    [InlineData("alice@example.com\r\nBcc: mallory@example.com")]
    [InlineData("Alice <alice@example.com>")]
    [InlineData("")]
    public async Task Invalid_email_address_skips_the_email_delivery(string address)
    {
        _harness.AddContact(_userId, email: address);

        var outcome = await ScheduleAsync(PasswordChanged(), OccurredAt);

        outcome.ShouldBe(ScheduleOutcome.Scheduled);
        _harness.Added.ShouldHaveSingleItem().Deliveries.ShouldHaveSingleItem().Channel.ShouldBe(NotificationChannel.InApp);
        var warning = _harness.Logger.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldContain(_userId.ToString());
        UserIdOnly(warning.Properties);
        if (address.Length > 0)
        {
            warning.Message.ShouldNotContain(address);
        }
    }

    [Fact]
    public async Task Invalid_email_address_on_an_email_only_type_records_the_inbox_and_creates_nothing()
    {
        _harness.AddContact(_userId, email: "not-an-address");

        var outcome = await ScheduleAsync(Notice(AuthNotificationTypes.RegistrationAttempted), contactVariables: null);

        outcome.ShouldBe(ScheduleOutcome.NothingToDeliver);
        _harness.Added.ShouldBeEmpty();
        _harness.Inbox.Received(1).Record(_messageId, Consumer);
        await _harness.UnitOfWork.Received(1).SaveChangesUnlessInboxDuplicateAsync(Ct);
        _harness.Metrics.DidNotReceiveWithAnyArgs().RecordCreated(default!);
    }

    [Fact]
    public async Task Every_channel_switched_off_records_the_inbox_and_creates_nothing()
    {
        _harness.AddContact(_userId);
        _harness.SetPreferences(
            _userId,
            UserPreference.Create(_userId, TestNotificationTypeSource.Welcome, NotificationChannel.Email, isEnabled: false, Now),
            UserPreference.Create(_userId, TestNotificationTypeSource.Welcome, NotificationChannel.InApp, isEnabled: false, Now));

        var outcome = await ScheduleAsync(Welcome());

        outcome.ShouldBe(ScheduleOutcome.NothingToDeliver);
        _harness.Added.ShouldBeEmpty();
        _harness.Inbox.Received(1).Record(_messageId, Consumer);
    }

    [Fact]
    public async Task Unknown_type_code_throws()
    {
        var exception = await Should.ThrowAsync<InvalidOperationException>(() => ScheduleAsync(Notice("auth.does_not_exist"), contactVariables: null));

        exception.Message.ShouldContain("auth.does_not_exist");
        _harness.Inbox.DidNotReceiveWithAnyArgs().Record(default, default!);
    }

    [Fact]
    public async Task Undeclared_or_missing_variables_throw_naming_only_the_variables()
    {
        _harness.AddContact(_userId);
        var request = Notice(AuthNotificationTypes.RegistrationAttempted) with
        {
            Variables = new Dictionary<string, string> { ["secret_note"] = "do-not-print" },
        };

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => ScheduleAsync(request, contactVariables: null));

        exception.Message.ShouldContain("secret_note");
        exception.Message.ShouldNotContain("do-not-print");
        _harness.Added.ShouldBeEmpty();
        await _harness.UnitOfWork.DidNotReceiveWithAnyArgs().SaveChangesUnlessInboxDuplicateAsync(Ct);
    }

    [Fact]
    public async Task Undeclared_protected_variable_throws()
    {
        var request = Notice(AuthNotificationTypes.RegistrationAttempted) with
        {
            ProtectedVariables = new Dictionary<string, string> { ["action_url"] = Ciphertext },
        };

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => ScheduleAsync(request, contactVariables: null));

        exception.Message.ShouldContain("action_url");
        exception.Message.ShouldNotContain(Ciphertext);
        _harness.SecretProtector.DidNotReceiveWithAnyArgs().Unprotect(default!);
    }

    private static IReadOnlyDictionary<string, string> OccurredAt(Modules.Auth.Contracts.Users.UserContact contact)
        => new Dictionary<string, string> { ["occurred_at"] = "2026-03-01 15:00", ["time_zone"] = "UTC" };

    private static Dictionary<string, string> Variables(string json) => JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;

    private static void UserIdOnly(IReadOnlyDictionary<string, object?> properties)
        => properties.Keys.Where(key => key != "{OriginalFormat}").ShouldBe(["UserId"]);

    private NotificationRequest Notice(string typeCode) =>
        new(typeCode, _userId, _messageId, Consumer, new Dictionary<string, string>(), new Dictionary<string, string>(), EmailOverride: null, ExpiresAt: null);

    private NotificationRequest PasswordChanged() => Notice(AuthNotificationTypes.PasswordChanged);

    private NotificationRequest PasswordReset() =>
        Notice(AuthNotificationTypes.PasswordReset) with
        {
            Variables = new Dictionary<string, string> { ["expires_in_minutes"] = "30" },
            ProtectedVariables = new Dictionary<string, string> { ["action_url"] = Ciphertext },
            ExpiresAt = Now.AddMinutes(30),
        };

    private NotificationRequest Welcome()
    {
        _harness.Decrypts(Ciphertext, PlaintextLink);
        return Notice(TestNotificationTypeSource.Welcome) with
        {
            Variables = new Dictionary<string, string> { ["occurred_at"] = "2026-03-01 23:30" },
            ProtectedVariables = new Dictionary<string, string> { ["action_url"] = Ciphertext },
        };
    }

    private Task<ScheduleOutcome> ScheduleAsync(
        NotificationRequest request,
        Func<Modules.Auth.Contracts.Users.UserContact, IReadOnlyDictionary<string, string>>? contactVariables = null)
        => _harness.CreateScheduler().ScheduleAsync(request, contactVariables, Ct);

    private Delivery Delivery(NotificationChannel channel)
        => _harness.Added.ShouldHaveSingleItem().Deliveries.Single(delivery => delivery.Channel == channel);

    private async Task AssertRefusedAsync(ScheduleOutcome outcome, string? plaintext)
    {
        outcome.ShouldBe(ScheduleOutcome.Refused);
        _harness.Added.ShouldBeEmpty();
        _harness.Inbox.Received(1).Record(_messageId, Consumer);
        await _harness.UnitOfWork.Received(1).SaveChangesUnlessInboxDuplicateAsync(Ct);
        _harness.Metrics.DidNotReceiveWithAnyArgs().RecordCreated(default!);

        var warning = _harness.Logger.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldContain(_messageId.ToString());
        warning.Message.ShouldContain(AuthNotificationTypes.PasswordReset);
        warning.Properties.Keys.Where(key => key != "{OriginalFormat}").Order(StringComparer.Ordinal).ShouldBe(["MessageId", "TypeCode"]);
        warning.Message.ShouldNotContain(Ciphertext);
        warning.Message.ShouldNotContain("alice@example.com");
        if (!string.IsNullOrEmpty(plaintext))
        {
            warning.Message.ShouldNotContain(plaintext);
        }
    }
}
