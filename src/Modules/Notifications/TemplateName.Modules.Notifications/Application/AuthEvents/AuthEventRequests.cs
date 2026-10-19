using System.Globalization;
using TemplateName.Modules.Auth.Contracts.Users;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Application.Scheduling;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Notifications.Application.AuthEvents;

/// <summary>
/// Builds the <see cref="NotificationRequest"/>s of the Auth event consumers. The inbox consumer name is the consuming handler's full
/// type name. Values are formatted here, because templates have no formatting functions: minutes as an invariant integer, local times
/// as <c>yyyy-MM-dd HH:mm</c> (invariant) in the recipient's time zone, with <c>time_zone</c> the recipient's IANA id or <c>UTC</c>
/// when that id is empty or unknown.
/// </summary>
internal static class AuthEventRequests
{
    private const string ExpiresInMinutes = "expires_in_minutes";
    private const string ActionUrl = "action_url";
    private const string TimeZone = "time_zone";
    private const string LocalTimeFormat = "yyyy-MM-dd HH:mm";
    private const string Utc = "UTC";

    private static readonly IReadOnlyDictionary<string, string> None = new Dictionary<string, string>();

    /// <summary>A notice without a link: only the variables the scheduler and the <c>contactVariables</c> callback add.</summary>
    public static NotificationRequest Notice(string typeCode, IIntegrationEvent integrationEvent, Guid userId, Type consumer) =>
        new(typeCode, userId, integrationEvent.Id, ConsumerName(consumer), None, None, EmailOverride: null, ExpiresAt: null);

    /// <summary>
    /// A single-use link: <c>action_url</c> is the event's ciphertext as is, the email goes to <paramref name="email"/> (the address the
    /// link was issued for), the notification expires with the link and <c>expires_in_minutes</c> is the time left, rounded up, at
    /// least 1.
    /// </summary>
    public static NotificationRequest Link(
        string typeCode,
        IIntegrationEvent integrationEvent,
        Guid userId,
        string email,
        string protectedActionUrl,
        DateTimeOffset expiresAt,
        DateTimeOffset now,
        Type consumer) =>
        new(
            typeCode,
            userId,
            integrationEvent.Id,
            ConsumerName(consumer),
            new Dictionary<string, string> { [ExpiresInMinutes] = MinutesLeft(expiresAt, now) },
            new Dictionary<string, string> { [ActionUrl] = protectedActionUrl },
            email,
            expiresAt);

    /// <summary>The <c>contactVariables</c> callback that adds <paramref name="variable"/> (<paramref name="instant"/> in the recipient's local time) and <c>time_zone</c>.</summary>
    public static Func<UserContact, IReadOnlyDictionary<string, string>> LocalTime(string variable, DateTimeOffset instant) =>
        contact =>
        {
            var (zone, isFallback) = TimeZoneResolver.Resolve(contact.TimeZone);
            return new Dictionary<string, string>
            {
                [variable] = TimeZoneInfo.ConvertTime(instant, zone).ToString(LocalTimeFormat, CultureInfo.InvariantCulture),
                [TimeZone] = isFallback ? Utc : contact.TimeZone.Trim(),
            };
        };

    private static string MinutesLeft(DateTimeOffset expiresAt, DateTimeOffset now) =>
        Math.Max(1L, (long)Math.Ceiling((expiresAt - now).TotalMinutes)).ToString(CultureInfo.InvariantCulture);

    private static string ConsumerName(Type consumer) =>
        consumer.FullName ?? throw new InvalidOperationException("A consumer type must have a full name.");
}
