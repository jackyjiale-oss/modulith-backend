using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Notifications.Application.Preferences.Get;

/// <summary>
/// Lists every catalog type with its channels in a fixed order: the types by code (ordinal), the channels by channel value. A channel
/// is enabled unless the caller has a stored row that switched it off (rows exist only for choices that differ from the default, D10);
/// a mandatory channel is always enabled, whatever a stale row says. Rows for a type or channel the catalog no longer has are ignored.
/// A caller without a user id has no rows, so the answer is the defaults.
/// </summary>
internal sealed class GetPreferencesQueryHandler(NotificationCatalog catalog, IPreferenceRepository preferences, ICurrentUser currentUser)
    : IQueryHandler<GetPreferencesQuery, PreferencesResponse>
{
    public async Task<Result<PreferencesResponse>> HandleAsync(GetPreferencesQuery query, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId ?? Guid.Empty;
        var switchedOff = (await preferences.ListAsync(userId, cancellationToken))
            .Where(preference => !preference.IsEnabled)
            .Select(preference => (preference.TypeCode, preference.Channel))
            .ToHashSet();

        var types = catalog.All
            .OrderBy(type => type.Code, StringComparer.Ordinal)
            .Select(type => new TypePreferenceResponse(
                type.Code,
                type.Category,
                [.. type.DefaultChannels.Order().Select(channel => new ChannelPreferenceResponse(
                    channel,
                    Enabled: type.IsMandatory(channel) || !switchedOff.Contains((type.Code, channel)),
                    Mandatory: type.IsMandatory(channel)))]))
            .ToList();

        return new PreferencesResponse(types);
    }
}
