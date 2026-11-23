using System.Text;
using System.Text.Json.Serialization;
using TemplateName.Modules.Notifications.Domain.PushDevices;

namespace TemplateName.Modules.Notifications.Endpoints;

/// <summary>The body of <c>POST /api/v1/notifications/devices</c>; the token is never printed.</summary>
internal sealed record RegisterDeviceRequest(
    [property: JsonRequired] string Token,
    [property: JsonRequired] PushPlatform Platform,
    string? DeviceName,
    string? AppVersion)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Platform = ").Append(Platform);
        return true;
    }
}
