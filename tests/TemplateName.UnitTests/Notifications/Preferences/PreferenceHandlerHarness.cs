using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Application.Common.Identity;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Application.Preferences.Get;
using TemplateName.Modules.Notifications.Application.Preferences.Update;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.Modules.Notifications.Domain.Preferences;

namespace TemplateName.UnitTests.Notifications.Preferences;

/// <summary>
/// The preference handlers over a substitute repository and the real catalog (Auth's types and <see cref="TestNotificationTypeSource"/>,
/// registered test-first so the order of the answer cannot be the order of registration). <c>auth.password_changed</c> has a mandatory
/// email and an optional in-app channel; <c>test.welcome</c> has two optional channels; <c>test.english_only</c> is in-app only.
/// </summary>
internal sealed class PreferenceHandlerHarness
{
    public const string PasswordChanged = "auth.password_changed";
    public const string Welcome = TestNotificationTypeSource.Welcome;

    public static readonly DateTimeOffset Now = new(2026, 10, 9, 9, 0, 0, TimeSpan.Zero);

    public PreferenceHandlerHarness()
    {
        CurrentUser.UserId.Returns(UserId);
        Preferences.ListAsync(UserId, Arg.Any<CancellationToken>()).Returns(_ => Stored.ToList());
        UnitOfWork.SaveChangesUnlessConflictAsync(Arg.Any<CancellationToken>()).Returns(true);
    }

    public Guid UserId { get; } = Guid.NewGuid();

    public ICurrentUser CurrentUser { get; } = Substitute.For<ICurrentUser>();

    public IPreferenceRepository Preferences { get; } = Substitute.For<IPreferenceRepository>();

    public IUnitOfWork UnitOfWork { get; } = Substitute.For<IUnitOfWork>();

    public NotificationCatalog Catalog { get; } = new([new TestNotificationTypeSource(), new AuthNotificationTypeSource()]);

    /// <summary>The rows the repository answers with; a test fills it before it calls a handler.</summary>
    public List<UserPreference> Stored { get; } = [];

    public GetPreferencesQueryHandler CreateGetHandler() => new(Catalog, Preferences, CurrentUser);

    public UpdatePreferencesCommandHandler CreateUpdateHandler()
        => new(Catalog, Preferences, CurrentUser, UnitOfWork, CreateGetHandler(), new FakeTimeProvider(Now));

    public UserPreference Disabled(string typeCode, NotificationChannel channel)
        => UserPreference.Create(UserId, typeCode, channel, isEnabled: false, Now);
}
