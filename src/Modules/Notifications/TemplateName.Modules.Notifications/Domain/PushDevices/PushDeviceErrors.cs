using TemplateName.SharedKernel;

namespace TemplateName.Modules.Notifications.Domain.PushDevices;

/// <summary>The push device errors. Each code has a message in <c>Resources/NotificationsErrorMessages.resx</c> and its translations.</summary>
internal static class PushDeviceErrors
{
    /// <summary>Also the answer for another user's device, so the response does not tell whether the id exists.</summary>
    public static Error NotFound(Guid id) =>
        Error.NotFound("notifications.device_not_found", $"Device '{id}' was not found.")
        with
        {
            Parameters = new Dictionary<string, object?> { ["id"] = id },
        };
}
