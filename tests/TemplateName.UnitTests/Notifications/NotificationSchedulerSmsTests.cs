using System.Text.Json;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Application.Scheduling;
using TemplateName.Modules.Notifications.Domain.Notifications;

namespace TemplateName.UnitTests.Notifications;

/// <summary>The scheduler's SMS branch: the destination, the switch, and the phone code type.</summary>
public sealed class NotificationSchedulerSmsTests
{
    private const string Consumer = "TemplateName.Tests.Consumer";
    private const string Ciphertext = "CfDJ8-protected-code";
    private const string PendingNumber = "+60123456789";
    private const string VerifiedNumber = "+60198765432";

    private static readonly DateTimeOffset Now = NotificationSchedulerHarness.Now;

    private readonly NotificationSchedulerHarness _harness = new();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _messageId = Guid.NewGuid();

    public NotificationSchedulerSmsTests()
    {
        _harness.Sms.Enabled = true;
        _harness.Decrypts(Ciphertext, "042137");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task One_sms_delivery_to_the_override_number()
    {
        _harness.AddContact(_userId, phoneNumber: VerifiedNumber);

        var outcome = await ScheduleAsync(PhoneCode(smsOverride: PendingNumber));

        outcome.ShouldBe(ScheduleOutcome.Scheduled);
        var notification = _harness.Added.ShouldHaveSingleItem();
        var delivery = notification.Deliveries.ShouldHaveSingleItem();
        delivery.Channel.ShouldBe(NotificationChannel.Sms);
        delivery.Destination.ShouldBe(PendingNumber);
        delivery.NextAttemptAt.ShouldBe(Now.UtcDateTime);
        notification.ExpiresAt.ShouldBe(Now.AddMinutes(10).UtcDateTime);
        JsonSerializer.Deserialize<Dictionary<string, string>>(notification.ProtectedData!)!["code"].ShouldBe(Ciphertext);
    }

    [Fact]
    public async Task Without_an_override_the_verified_contact_number_is_used()
    {
        _harness.AddContact(_userId, phoneNumber: VerifiedNumber);

        var outcome = await ScheduleAsync(PhoneCode(smsOverride: null));

        outcome.ShouldBe(ScheduleOutcome.Scheduled);
        _harness.Added.ShouldHaveSingleItem().Deliveries.ShouldHaveSingleItem().Destination.ShouldBe(VerifiedNumber);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0123456789")]
    [InlineData("+0123456789")]
    [InlineData("+60123")]
    [InlineData("+6012345678901234")]
    [InlineData("+60 123456789")]
    public async Task No_valid_number_skips_sms(string? number)
    {
        _harness.AddContact(_userId, phoneNumber: null);

        var outcome = await ScheduleAsync(PhoneCode(smsOverride: number));

        outcome.ShouldBe(ScheduleOutcome.NothingToDeliver);
        _harness.Added.ShouldBeEmpty();
        var warning = _harness.Logger.Entries.First(entry => entry.Level == LogLevel.Warning);
        warning.Properties.Keys.Where(key => key != "{OriginalFormat}").ShouldBe(["UserId"]);
        if (number is not null)
        {
            warning.Message.ShouldNotContain(number);
        }
    }

    [Fact]
    public async Task Sms_off_records_the_code_event_once()
    {
        _harness.Sms.Enabled = false;
        _harness.AddContact(_userId, phoneNumber: VerifiedNumber);

        var outcome = await ScheduleAsync(PhoneCode(smsOverride: PendingNumber));

        outcome.ShouldBe(ScheduleOutcome.NothingToDeliver);
        _harness.Added.ShouldBeEmpty();
        _harness.Inbox.Received(1).Record(_messageId, Consumer);
        await _harness.UnitOfWork.Received(1).SaveChangesUnlessInboxDuplicateAsync(Ct);
    }

    [Fact]
    public async Task The_phone_code_is_never_deferred_by_quiet_hours()
    {
        _harness.AddContact(_userId, timeZone: "Asia/Kuala_Lumpur");
        _harness.SetQuietHours(_userId, new TimeOnly(22, 0), new TimeOnly(7, 0));

        await ScheduleAsync(PhoneCode(smsOverride: PendingNumber));

        _harness.Added.ShouldHaveSingleItem().Deliveries.ShouldHaveSingleItem().NextAttemptAt.ShouldBe(Now.UtcDateTime);
    }

    [Fact]
    public void The_request_never_prints_the_number()
    {
        var text = PhoneCode(smsOverride: PendingNumber).ToString();

        text.ShouldContain("HasSmsOverride = True");
        text.ShouldNotContain("60123456789");
    }

    private NotificationRequest PhoneCode(string? smsOverride) =>
        new(
            AuthNotificationTypes.PhoneVerification,
            _userId,
            _messageId,
            Consumer,
            new Dictionary<string, string> { ["expires_in_minutes"] = "10" },
            new Dictionary<string, string> { ["code"] = Ciphertext },
            EmailOverride: null,
            ExpiresAt: Now.AddMinutes(10),
            SmsOverride: smsOverride);

    private Task<ScheduleOutcome> ScheduleAsync(NotificationRequest request)
        => _harness.CreateScheduler().ScheduleAsync(request, contactVariables: null, Ct);
}
