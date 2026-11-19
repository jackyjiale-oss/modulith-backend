using TemplateName.Modules.Notifications.Application.Preferences;
using TemplateName.Modules.Notifications.Application.Preferences.Get;
using TemplateName.Modules.Notifications.Domain.Notifications;

namespace TemplateName.UnitTests.Notifications.Preferences;

public sealed class PreferenceVersionTests
{
    private static PreferencesResponse View(bool emailEnabled)
        => new([new TypePreferenceResponse("a.type", NotificationCategory.Account, [
            new ChannelPreferenceResponse(NotificationChannel.Email, emailEnabled, false),
            new ChannelPreferenceResponse(NotificationChannel.InApp, true, false)])]);

    [Fact]
    public void Preferences_cover_every_channel_state()
    {
        PreferencesVersion.Of(View(true)).ShouldNotBe(PreferencesVersion.Of(View(false)));
        PreferencesVersion.Of(View(true)).ShouldBe(PreferencesVersion.Of(View(true)));
    }

    [Fact]
    public void Quiet_hours_cover_start_and_end()
    {
        var window = QuietHoursVersion.Of(new TimeOnly(22, 0), new TimeOnly(7, 0));

        QuietHoursVersion.Of(new TimeOnly(23, 0), new TimeOnly(7, 0)).ShouldNotBe(window);
        QuietHoursVersion.Of(new TimeOnly(22, 0), new TimeOnly(6, 0)).ShouldNotBe(window);
        QuietHoursVersion.Of(null, null).ShouldNotBe(window);
        QuietHoursVersion.Of(new TimeOnly(22, 0), new TimeOnly(7, 0)).ShouldBe(window);
    }
}
