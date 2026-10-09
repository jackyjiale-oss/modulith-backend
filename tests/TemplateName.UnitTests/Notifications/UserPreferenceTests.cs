using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.Modules.Notifications.Domain.Preferences;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Notifications;

public sealed class UserPreferenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_and_set_enabled_stamp_the_time()
    {
        var userId = Guid.NewGuid();

        var preference = UserPreference.Create(userId, "auth.password_changed", NotificationChannel.InApp, isEnabled: false, Now);

        preference.UserId.ShouldBe(userId);
        preference.TypeCode.ShouldBe("auth.password_changed");
        preference.Channel.ShouldBe(NotificationChannel.InApp);
        preference.IsEnabled.ShouldBeFalse();
        preference.UpdatedAt.ShouldBe(Now);

        preference.SetEnabled(true, Now.AddHours(1));

        preference.IsEnabled.ShouldBeTrue();
        preference.UpdatedAt.ShouldBe(Now.AddHours(1));
    }

    [Fact]
    public void Errors_carry_their_parameters_and_types()
    {
        PreferenceErrors.InvalidQuietHours.Code.ShouldBe("notifications.invalid_quiet_hours");
        PreferenceErrors.InvalidQuietHours.Type.ShouldBe(ErrorType.Validation);

        var unknown = PreferenceErrors.UnknownType("billing.invoice");
        unknown.Code.ShouldBe("notifications.unknown_type");
        unknown.Type.ShouldBe(ErrorType.Validation);
        unknown.Parameters.ShouldNotBeNull()["typeCode"].ShouldBe("billing.invoice");

        var unsupported = PreferenceErrors.ChannelNotSupported("auth.password_changed", "InApp");
        unsupported.Code.ShouldBe("notifications.channel_not_supported");
        unsupported.Type.ShouldBe(ErrorType.Validation);
        unsupported.Parameters.ShouldNotBeNull()["typeCode"].ShouldBe("auth.password_changed");
        unsupported.Parameters["channel"].ShouldBe("InApp");

        var mandatory = PreferenceErrors.ChannelMandatory("auth.password_changed", "Email");
        mandatory.Code.ShouldBe("notifications.channel_mandatory");
        mandatory.Type.ShouldBe(ErrorType.Validation);
        mandatory.Parameters.ShouldNotBeNull()["typeCode"].ShouldBe("auth.password_changed");
        mandatory.Parameters["channel"].ShouldBe("Email");
    }
}
