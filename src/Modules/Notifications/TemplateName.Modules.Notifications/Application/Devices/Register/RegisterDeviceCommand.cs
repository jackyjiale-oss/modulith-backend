using System.Text;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Notifications.Domain.PushDevices;

namespace TemplateName.Modules.Notifications.Application.Devices.Register;

/// <summary>Registers the caller's device for push, upserted by <paramref name="Token"/>; the token is never printed.</summary>
internal sealed record RegisterDeviceCommand(PushPlatform Platform, string Token, string? DeviceName, string? AppVersion)
    : ICommand<DeviceResponse>
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Platform = ").Append(Platform)
            .Append(", HasDeviceName = ").Append(DeviceName is not null)
            .Append(", HasAppVersion = ").Append(AppVersion is not null);
        return true;
    }
}
