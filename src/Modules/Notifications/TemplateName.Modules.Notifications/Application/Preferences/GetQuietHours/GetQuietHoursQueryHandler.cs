using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Contracts.Users;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Notifications.Application.Preferences.GetQuietHours;

/// <summary>
/// Reads the caller's window from their profile (none when they never set one) and the time zone from Auth's
/// <see cref="IUserContactDirectory"/>: the stored IANA id as it is, or <c>UTC</c> when it is empty, unknown or the directory does not
/// know the user, the same fallback <see cref="TimeZoneResolver"/> gives the scheduler. Nothing is logged here (the scheduler warns
/// when it actually applies the fallback).
/// </summary>
internal sealed class GetQuietHoursQueryHandler(IPreferenceRepository preferences, IUserContactDirectory contacts, ICurrentUser currentUser)
    : IQueryHandler<GetQuietHoursQuery, QuietHoursResponse>
{
    private const string FallbackTimeZone = "UTC";

    public async Task<Result<QuietHoursResponse>> HandleAsync(GetQuietHoursQuery query, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId ?? Guid.Empty;
        var profile = await preferences.GetSettingsAsync(userId, cancellationToken);
        var contact = await contacts.FindAsync(userId, cancellationToken);

        var (_, isFallback) = TimeZoneResolver.Resolve(contact?.TimeZone);
        var timeZone = isFallback ? FallbackTimeZone : contact!.TimeZone.Trim();

        return new QuietHoursResponse(profile?.QuietHours?.Start, profile?.QuietHours?.End, timeZone);
    }
}
