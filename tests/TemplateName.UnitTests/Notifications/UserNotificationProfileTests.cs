using TemplateName.Modules.Notifications.Domain.Preferences;

namespace TemplateName.UnitTests.Notifications;

public sealed class UserNotificationProfileTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_starts_without_quiet_hours_and_is_keyed_by_the_user()
    {
        var userId = Guid.NewGuid();

        var profile = UserNotificationProfile.Create(userId, Now);

        profile.Id.ShouldBe(userId);
        profile.UserId.ShouldBe(userId);
        profile.QuietHours.ShouldBeNull();
        profile.UpdatedAt.ShouldBe(Now);
    }

    [Fact]
    public void SetQuietHours_stores_the_window_and_null_clears_it()
    {
        var profile = UserNotificationProfile.Create(Guid.NewGuid(), Now);
        var window = QuietHours.Create(new TimeOnly(22, 0), new TimeOnly(7, 0)).Value;

        profile.SetQuietHours(window, Now.AddMinutes(1));

        profile.QuietHours.ShouldBe(window);
        profile.UpdatedAt.ShouldBe(Now.AddMinutes(1));

        profile.SetQuietHours(null, Now.AddMinutes(2));

        profile.QuietHours.ShouldBeNull();
        profile.UpdatedAt.ShouldBe(Now.AddMinutes(2));
    }
}
