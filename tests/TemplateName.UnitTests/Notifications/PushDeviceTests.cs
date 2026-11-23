using System.Security.Cryptography;
using System.Text;
using TemplateName.Modules.Notifications.Domain.PushDevices;

namespace TemplateName.UnitTests.Notifications;

public sealed class PushDeviceTests
{
    private const string Token = "fcm-token-AbC123:xyz";

    private static readonly DateTimeOffset Now = new(2026, 11, 23, 8, 0, 0, TimeSpan.Zero);

    private static readonly Guid UserId = Guid.NewGuid();

    private static readonly Guid SessionId = Guid.NewGuid();

    private static PushDevice Register(string? deviceName = "Pixel 9", string? appVersion = "1.4.0") =>
        PushDevice.Register(UserId, SessionId, PushPlatform.Android, Token, deviceName, appVersion, Now);

    [Fact]
    public void Register_sets_the_hash_of_the_token()
    {
        var device = Register();

        device.Id.ShouldNotBe(Guid.Empty);
        device.UserId.ShouldBe(UserId);
        device.SessionId.ShouldBe(SessionId);
        device.Platform.ShouldBe(PushPlatform.Android);
        device.Token.ShouldBe(Token);
        device.TokenHash.ShouldBe(SHA256.HashData(Encoding.UTF8.GetBytes(Token)));
        PushDevice.HashOf(Token).ShouldBe(device.TokenHash);
        device.DeviceName.ShouldBe("Pixel 9");
        device.AppVersion.ShouldBe("1.4.0");
        device.CreatedAt.ShouldBe(Now);
        device.LastRegisteredAt.ShouldBe(Now);
        device.IsActive.ShouldBeTrue();
        device.DeactivatedAt.ShouldBeNull();
        device.DeactivationReason.ShouldBeNull();
    }

    [Fact]
    public void Register_cuts_name_and_version()
    {
        var device = Register(new string('n', 101), new string('v', 51));

        device.DeviceName!.Length.ShouldBe(PushDevice.MaxDeviceNameLength);
        device.AppVersion!.Length.ShouldBe(PushDevice.MaxAppVersionLength);
    }

    [Fact]
    public void Reregister_moves_the_device_and_reactivates_it()
    {
        var device = Register();
        device.Deactivate(PushDeviceDeactivationReason.SessionEnded, Now.AddHours(1));
        var newOwner = Guid.NewGuid();
        var newSession = Guid.NewGuid();
        var later = Now.AddDays(1);

        device.Reregister(newOwner, newSession, PushPlatform.Ios, "iPhone", null, later);

        device.UserId.ShouldBe(newOwner);
        device.SessionId.ShouldBe(newSession);
        device.Platform.ShouldBe(PushPlatform.Ios);
        device.DeviceName.ShouldBe("iPhone");
        device.AppVersion.ShouldBeNull();
        device.LastRegisteredAt.ShouldBe(later);
        device.CreatedAt.ShouldBe(Now);
        device.IsActive.ShouldBeTrue();
        device.DeactivatedAt.ShouldBeNull();
        device.DeactivationReason.ShouldBeNull();
    }

    [Fact]
    public void Deactivate_keeps_the_first_reason()
    {
        var device = Register();

        device.Deactivate(PushDeviceDeactivationReason.RejectedByProvider, Now.AddMinutes(1));
        device.Deactivate(PushDeviceDeactivationReason.RemovedByUser, Now.AddMinutes(2));

        device.IsActive.ShouldBeFalse();
        device.DeactivatedAt.ShouldBe(Now.AddMinutes(1));
        device.DeactivationReason.ShouldBe(PushDeviceDeactivationReason.RejectedByProvider);
    }

    [Fact]
    public void ToString_never_contains_the_token() => Register().ToString().ShouldNotContain(Token);

    [Fact]
    public void A_blank_token_is_refused() =>
        Should.Throw<ArgumentException>(() => PushDevice.Register(UserId, SessionId, PushPlatform.Web, " ", null, null, Now));

    [Fact]
    public void Not_found_names_the_id()
    {
        var id = Guid.NewGuid();

        var error = PushDeviceErrors.NotFound(id);

        error.Code.ShouldBe("notifications.device_not_found");
        error.Parameters!["id"].ShouldBe(id);
    }
}
