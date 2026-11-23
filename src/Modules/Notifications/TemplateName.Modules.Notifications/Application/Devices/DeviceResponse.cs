using TemplateName.Modules.Notifications.Domain.PushDevices;

namespace TemplateName.Modules.Notifications.Application.Devices;

/// <summary>A registered device as the API shows it: never its token.</summary>
internal sealed record DeviceResponse(Guid Id, PushPlatform Platform, string? DeviceName, string? AppVersion, DateTimeOffset RegisteredAt)
{
    public static DeviceResponse From(PushDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return new DeviceResponse(device.Id, device.Platform, device.DeviceName, device.AppVersion, device.LastRegisteredAt);
    }
}
