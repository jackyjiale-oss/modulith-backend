using Microsoft.Extensions.Options;
using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Application.Devices;
using TemplateName.Modules.Notifications.Application.Sms;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Notifications.Application.Preferences.Get;

/// <summary>
/// Lists every catalog type with its channels in a fixed order: the types by code (ordinal), the channels by channel value. A channel
/// is enabled unless the caller has a stored row that switched it off (rows exist only for choices that differ from the default, D10);
/// a mandatory channel is always enabled, whatever a stale row says. Rows for a type or channel the catalog no longer has are ignored.
/// A caller without a user id has no rows, so the answer is the defaults. The push channel is listed only while push is enabled.
/// </summary>
internal sealed class GetPreferencesQueryHandler(
    NotificationCatalog catalog,
    IPreferenceRepository preferences,
    ICurrentUser currentUser,
    IOptions<PushOptions> pushOptions,
    IOptions<SmsOptions> smsOptions) : IQueryHandler<GetPreferencesQuery, PreferencesResponse>
{
    public async Task<Result<PreferencesResponse>> HandleAsync(GetPreferencesQuery query, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId ?? Guid.Empty;
        return PreferencesView.Build(catalog, await preferences.ListAsync(userId, cancellationToken), pushOptions.Value.Enabled, smsOptions.Value.Enabled);
    }
}
