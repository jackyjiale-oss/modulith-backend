using System.Diagnostics.CodeAnalysis;

namespace TemplateName.Modules.Notifications.Domain.Deliveries;

/// <summary>
/// The destination of a push delivery: the id of the device it goes to, in <c>"D"</c> format, never the token. The token is looked up
/// when the delivery is sent, so a delivery row (and the administrator's list) never holds it.
/// </summary>
internal static class PushDestination
{
    public static string For(Guid deviceId) => deviceId.ToString("D");

    public static bool IsValid([NotNullWhen(true)] string? destination) => Guid.TryParseExact(destination, "D", out _);

    public static Guid DeviceIdOf(string destination) => Guid.ParseExact(destination, "D");
}
