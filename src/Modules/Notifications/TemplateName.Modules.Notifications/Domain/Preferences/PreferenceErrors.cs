using TemplateName.SharedKernel;

namespace TemplateName.Modules.Notifications.Domain.Preferences;

/// <summary>The preference errors. Each code has a message in <c>Resources/NotificationsErrorMessages.resx</c> and its translations.</summary>
internal static class PreferenceErrors
{
    /// <summary>Quiet hours that start and end at the same time would mean either all day or never.</summary>
    public static readonly Error InvalidQuietHours = Error.Validation(
        "notifications.invalid_quiet_hours",
        "The quiet hours must start and end at different times.");

    public static Error UnknownType(string typeCode) =>
        Error.Validation("notifications.unknown_type", $"Notification type '{typeCode}' does not exist.")
        with
        {
            Parameters = new Dictionary<string, object?> { ["typeCode"] = typeCode },
        };

    public static Error ChannelNotSupported(string typeCode, string channel) =>
        Error.Validation(
            "notifications.channel_not_supported",
            $"Notification type '{typeCode}' is not delivered through the '{channel}' channel.")
        with
        {
            Parameters = new Dictionary<string, object?> { ["typeCode"] = typeCode, ["channel"] = channel },
        };

    /// <summary>A security notice's email cannot be switched off (blueprint 10.6).</summary>
    public static Error ChannelMandatory(string typeCode, string channel) =>
        Error.Validation(
            "notifications.channel_mandatory",
            $"The '{channel}' channel cannot be switched off for notification type '{typeCode}'.")
        with
        {
            Parameters = new Dictionary<string, object?> { ["typeCode"] = typeCode, ["channel"] = channel },
        };
}
