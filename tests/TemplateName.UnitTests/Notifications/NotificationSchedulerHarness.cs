using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Application.Common.Security;
using TemplateName.Modules.Auth.Contracts.Users;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Application.Scheduling;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.Modules.Notifications.Domain.Preferences;
using TemplateName.UnitTests.Application;

namespace TemplateName.UnitTests.Notifications;

/// <summary>
/// A <see cref="NotificationScheduler"/> over substitutes and the real catalog (Auth's types and <see cref="TestNotificationTypeSource"/>,
/// whose <c>test.welcome</c> is a <c>Normal</c> type with optional email and in-app channels). Every notification the scheduler adds is
/// kept in <see cref="Added"/>; the save succeeds unless a test says otherwise.
/// </summary>
internal sealed class NotificationSchedulerHarness
{
    /// <summary>2026-03-01 15:30 UTC: 23:30 in Kuala Lumpur, inside quiet hours of 22:00 to 07:00 there.</summary>
    public static readonly DateTimeOffset Now = new(2026, 3, 1, 15, 30, 0, TimeSpan.Zero);

    public NotificationSchedulerHarness()
    {
        UnitOfWork.SaveChangesUnlessInboxDuplicateAsync(Arg.Any<CancellationToken>()).Returns(true);
        Notifications.When(repository => repository.Add(Arg.Any<Notification>())).Do(call => Added.Add(call.Arg<Notification>()));
        Preferences.ListAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<UserPreference>());
        Preferences.GetSettingsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((UserNotificationProfile?)null);
    }

    public IInbox Inbox { get; } = Substitute.For<IInbox>();

    public IUserContactDirectory Contacts { get; } = Substitute.For<IUserContactDirectory>();

    public IPreferenceRepository Preferences { get; } = Substitute.For<IPreferenceRepository>();

    public INotificationRepository Notifications { get; } = Substitute.For<INotificationRepository>();

    public IUnitOfWork UnitOfWork { get; } = Substitute.For<IUnitOfWork>();

    public ISecretProtector SecretProtector { get; } = Substitute.For<ISecretProtector>();

    public INotificationsMetrics Metrics { get; } = Substitute.For<INotificationsMetrics>();

    public FakeTimeProvider Time { get; } = new(Now);

    public RecordingLogger<NotificationScheduler> Logger { get; } = new();

    public NotificationCatalog Catalog { get; } = new([new AuthNotificationTypeSource(), new TestNotificationTypeSource()]);

    public List<Notification> Added { get; } = [];

    public NotificationScheduler CreateScheduler() =>
        new(Catalog, Inbox, Contacts, Preferences, Notifications, UnitOfWork, SecretProtector, Metrics, Time, Logger);

    /// <summary>Makes the directory know a user and returns the contact.</summary>
    public UserContact AddContact(
        Guid userId,
        string email = "alice@example.com",
        string displayName = "Alice",
        string locale = "ms",
        string timeZone = "UTC")
    {
        var contact = new UserContact(userId, email, displayName, locale, timeZone);
        Contacts.FindAsync(userId, Arg.Any<CancellationToken>()).Returns(contact);
        return contact;
    }

    public void SetQuietHours(Guid userId, TimeOnly start, TimeOnly end)
    {
        var profile = UserNotificationProfile.Create(userId, Now);
        profile.SetQuietHours(QuietHours.Create(start, end).Value, Now);
        Preferences.GetSettingsAsync(userId, Arg.Any<CancellationToken>()).Returns(profile);
    }

    public void SetPreferences(Guid userId, params UserPreference[] preferences)
        => Preferences.ListAsync(userId, Arg.Any<CancellationToken>()).Returns(preferences);

    /// <summary>Makes the protector decrypt <paramref name="ciphertext"/> to <paramref name="plaintext"/>.</summary>
    public void Decrypts(string ciphertext, string plaintext) => SecretProtector.Unprotect(ciphertext).Returns(plaintext);
}
