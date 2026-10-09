namespace TemplateName.Modules.Notifications.Application.Catalog;

/// <summary>Finds the time zone of a recipient from the IANA id Auth stores; an unknown, blank or missing id means <c>UTC</c>.</summary>
internal static class TimeZoneResolver
{
    /// <summary>
    /// The zone for <paramref name="ianaId"/> as the runtime resolves it (<c>Asia/Kuala_Lumpur</c>; a runtime that also maps Windows ids
    /// accepts those too), else <see cref="TimeZoneInfo.Utc"/> with <c>IsFallback</c> true so the caller can log it.
    /// </summary>
    public static (TimeZoneInfo Zone, bool IsFallback) Resolve(string? ianaId)
    {
        if (!string.IsNullOrWhiteSpace(ianaId) && TimeZoneInfo.TryFindSystemTimeZoneById(ianaId.Trim(), out var zone))
        {
            return (zone, false);
        }

        return (TimeZoneInfo.Utc, true);
    }
}
