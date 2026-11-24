using Microsoft.Extensions.Logging;
using NSubstitute;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Application.Scheduling;

namespace TemplateName.UnitTests.Notifications;

/// <summary>The scheduler checks each secret by its kind: a <see cref="SecretKind.Code"/> is 4 to 10 ASCII digits, a link is a safe URL.</summary>
public sealed class NotificationSchedulerSecretKindTests
{
    private const string Consumer = "TemplateName.Tests.Consumer";
    private const string Ciphertext = "CfDJ8-protected-code";

    private readonly NotificationSchedulerHarness _harness = new();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _messageId = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("1234")]
    [InlineData("042137")]
    [InlineData("0123456789")]
    public async Task A_code_of_four_to_ten_digits_is_scheduled(string plaintext)
    {
        _harness.AddContact(_userId);
        _harness.Decrypts(Ciphertext, plaintext);

        var outcome = await ScheduleAsync(CodeNotice());

        outcome.ShouldBe(ScheduleOutcome.Scheduled);
        _harness.Added.ShouldHaveSingleItem().ProtectedData!.ShouldNotContain(plaintext);
    }

    [Theory]
    [InlineData("12a456")]
    [InlineData("123")]
    [InlineData("12345678901")]
    [InlineData("１２３４５６")]
    [InlineData("12 456")]
    [InlineData("")]
    [InlineData("https://app.example.test/reset?token=1")]
    public async Task A_code_secret_must_be_digits(string plaintext)
    {
        _harness.AddContact(_userId);
        _harness.Decrypts(Ciphertext, plaintext);

        var outcome = await ScheduleAsync(CodeNotice());

        outcome.ShouldBe(ScheduleOutcome.Refused);
        _harness.Added.ShouldBeEmpty();
        _harness.Inbox.Received(1).Record(_messageId, Consumer);
        var warning = _harness.Logger.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Properties["Reason"].ShouldBe(NotificationScheduler.UnsafeCodeReason);
        warning.Properties["TypeCode"].ShouldBe(CodeTestTypeSource.CodeNotice);
        if (plaintext.Length > 0)
        {
            // The random event id could hold the digits by chance; the rest of the line must not.
            warning.Message.Replace(_messageId.ToString(), string.Empty, StringComparison.Ordinal).ShouldNotContain(plaintext);
        }
    }

    [Fact]
    public async Task A_link_secret_is_still_a_safe_url()
    {
        _harness.AddContact(_userId);
        _harness.Decrypts(Ciphertext, "042137");

        var outcome = await ScheduleAsync(new NotificationRequest(
            AuthNotificationTypes.PasswordReset,
            _userId,
            _messageId,
            Consumer,
            new Dictionary<string, string> { ["expires_in_minutes"] = "30" },
            new Dictionary<string, string> { ["action_url"] = Ciphertext },
            EmailOverride: null,
            ExpiresAt: null));

        outcome.ShouldBe(ScheduleOutcome.Refused);
        _harness.Logger.Entries.ShouldHaveSingleItem().Properties["Reason"].ShouldBe(NotificationScheduler.UnsafeUrlReason);
    }

    [Fact]
    public void Unsafe_code_is_a_reason_code() => NotificationScheduler.UnsafeCodeReason.ShouldBe("unsafe_code");

    private NotificationRequest CodeNotice() =>
        new(
            CodeTestTypeSource.CodeNotice,
            _userId,
            _messageId,
            Consumer,
            new Dictionary<string, string>(),
            new Dictionary<string, string> { ["code"] = Ciphertext },
            EmailOverride: null,
            ExpiresAt: null);

    private Task<ScheduleOutcome> ScheduleAsync(NotificationRequest request)
        => _harness.CreateScheduler().ScheduleAsync(request, contactVariables: null, Ct);
}
